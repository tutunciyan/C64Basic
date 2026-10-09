using C64Basic.Core.Machine;

namespace C64Basic.Core.Rom;

/// <summary>
/// The serial (IEC) bus between the C64 and its drives: three open-collector lines, ATN, CLK and DATA. A line is low when any
/// party pulls it low, so each line is the wired AND of everyone's outputs. The C64 pulls a line low by setting a bit in CIA 2's
/// port A (PA3 = ATN, PA4 = CLK, PA5 = DATA, each through an inverter) and reads CLK and DATA back on PA6 and PA7.
/// A 1541 pulls CLK and DATA low from VIA 1 port B (PB3 = CLK, PB1 = DATA) and reads all three inverted on PB2, PB0 and PB7. It also
/// has a bit of hardware of its own: DATA is pulled low automatically whenever ATN is asserted and the ATNA bit (PB4) does not
/// match it, which is how a drive answers a command within a millisecond whatever the processor is doing.
/// </summary>
public sealed class IecBus
{
    readonly Cia2 _cia;
    readonly List<Drive1541> _drives = new();

    /// <summary>
    /// Seconds a line takes to rise after everybody let go of it. A line is only pulled up by a resistor, through the capacitance of the
    /// cable, so a release is slower than a pull; pulling low is immediate. Fast loaders count on this: their timing windows only hold
    /// with a release that is seen a little late. Zero is an ideal bus.
    /// </summary>
    public double RiseDelay { get; set; }

    /// <summary>Line levels from the outputs of everybody on the bus: true is high (released), false is low (asserted).</summary>
    public bool Atn { get; private set; } = true;

    public bool Clk => !ClkLow();
    public bool Data => !DataLow();

    // when each line was last let go (raw level rose), in seconds
    double _clkRose = double.NegativeInfinity, _dataRose = double.NegativeInfinity, _atnRose = double.NegativeInfinity;
    bool _clkHigh = true, _dataHigh = true;

    /// <summary>The level of CLK as a device sampling it at <paramref name="seconds"/> sees it, after the rise delay.</summary>
    public bool ClkAt(double seconds) => Clk && seconds >= _clkRose + RiseDelay;

    public bool DataAt(double seconds) => Data && seconds >= _dataRose + RiseDelay;

    public bool AtnAt(double seconds) => Atn && seconds >= _atnRose + RiseDelay;

    public IecBus(Cia2 cia)
    {
        _cia = cia;
        cia.Iec = this;
        cia.PortAChanged += () => Update(_cia.BusSeconds);
    }

    /// <summary>Connects a drive as device <paramref name="device"/> (8-11, set by the two jumpers on PB5 and PB6).</summary>
    public void Attach(Drive1541 drive, int device = 8)
    {
        if (device is < 8 or > 11) throw new ArgumentOutOfRangeException(nameof(device), "a 1541 is device 8 to 11");
        _drives.Add(drive);
        drive.Via1.PinsB = () => DrivePins(device, DriveTime(drive));
        drive.Via1.PortBChanged += () => Update(DriveTime(drive));
        Update(_cia.BusSeconds);
        drive.Via1.SetCa1(!Atn);
    }

    /// <summary>The drive's bus access in seconds (the same half-cycle offset the C64's chips use).</summary>
    static double DriveTime(Drive1541 drive) => (drive.Cpu.AccessCycle + 0.5) / Drive1541.ClockHz;

    public void SaveState(BinaryWriter w)
    {
        w.Write(Atn); w.Write(_clkHigh); w.Write(_dataHigh);
        w.Write(_clkRose); w.Write(_dataRose); w.Write(_atnRose);
    }

    public void LoadState(BinaryReader r)
    {
        Atn = r.ReadBoolean(); _clkHigh = r.ReadBoolean(); _dataHigh = r.ReadBoolean();
        _clkRose = r.ReadDouble(); _dataRose = r.ReadDouble(); _atnRose = r.ReadDouble();
    }

    bool AtnPulledByC64 => (_cia.PortAOutput & 0x08) != 0;

    bool ClkLow()
    {
        if ((_cia.PortAOutput & 0x10) != 0) return true;
        foreach (var d in _drives) if ((d.Via1.PortBOutput & 0x08) != 0) return true;
        return false;
    }

    bool DataLow()
    {
        if ((_cia.PortAOutput & 0x20) != 0) return true;
        foreach (var d in _drives)
        {
            byte pb = d.Via1.PortBOutput;
            if ((pb & 0x02) != 0) return true;                       // the processor pulls DATA
            bool atna = (pb & 0x10) != 0;
            if (atna == Atn) return true;                            // the ATN acknowledge hardware: asserted ATN without ATNA, or released with it
        }
        return false;
    }

    /// <summary>What port B of a drive's VIA 1 reads: the three lines inverted on PB0, PB2 and PB7, the device number on PB5-PB6.</summary>
    byte DrivePins(int device, double now)
    {
        int pins = 0x1A;                                             // PB1, PB3, PB4 are outputs
        if (!DataAt(now)) pins |= 0x01;
        if (!ClkAt(now)) pins |= 0x04;
        if (!AtnAt(now)) pins |= 0x80;
        pins |= (device - 8) << 5;
        return (byte)pins;
    }

    /// <summary>Called whenever any party's outputs may have changed, at the time given; a change of ATN is an edge on every drive's CA1.</summary>
    void Update(double now)
    {
        bool atn = !AtnPulledByC64, clk = Clk, data = Data;
        if (clk && !_clkHigh) _clkRose = now;
        if (data && !_dataHigh) _dataRose = now;
        _clkHigh = clk;
        _dataHigh = data;
        if (atn == Atn) return;
        Atn = atn;
        if (atn) _atnRose = now;
        foreach (var d in _drives) d.Via1.SetCa1(!atn);
    }
}
