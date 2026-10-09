namespace C64Basic.Core.Machine;

/// <summary>Keys and joysticks as the host sees them; the CIA 1 and the keyboard scanner read them.</summary>
public interface IInputDevice
{
    /// <summary>
    /// The keys pressed in one column of the keyboard matrix: bit n is set when the key at row n is down.
    /// Column and row follow the hardware (column = port A bit, row = port B bit), so key number
    /// <c>column * 8 + row</c> is the KERNAL's key index (0 = DEL, 1 = RETURN, 60 = SPACE ...).
    /// </summary>
    byte KeyColumn(int column);

    /// <summary>Joystick state for port 1 or 2: bit 0 up, 1 down, 2 left, 3 right, 4 fire; a set bit means pressed.</summary>
    byte Joystick(int port);

    /// <summary>
    /// A paddle on game port 1 or 2: <paramref name="axis"/> 0 is the one the SID reads at $D419 (POTX), 1 the one at $D41A (POTY).
    /// 0-255, larger = turned further clockwise. The fire buttons are the joystick's left and right bits.
    /// </summary>
    byte Paddle(int port, int axis) => 0;

    /// <summary>True while the RESTORE key is held: it pulls the NMI line, and with RUN/STOP it is the warm start.</summary>
    bool Restore => false;
}

/// <summary>
/// A 6526 Complex Interface Adapter: two 8-bit ports, two 16-bit timers, a time-of-day clock and an interrupt
/// register, mirrored every 16 bytes through its 256-byte window. Timers advance lazily from the bus clock when
/// a register is touched. The timers can drive PB6/PB7 (pulse or toggle), the shift register sends bytes on timer A and
/// takes them from the host, and the time of day raises its alarm interrupt. The host can pulse the CNT pin for the timers.
/// </summary>
public class Cia : IMemoryMapped
{
    public const double ClockHz = 985248;
    public const int Length = 0x100;

    protected readonly Bus Bus;
    readonly int _start;

    protected byte Pra, Prb, Ddra, Ddrb;
    byte _sdr, _flags, _mask;
    readonly Timer _a = new(), _b = new();
    long _lastCycle;

    // time of day: seconds since midnight at the moment _todAt, held while latched or stopped
    double _todBase, _todAt;
    bool _todStopped, _todLatched;
    int _todLatch;
    readonly byte[] _alarm = new byte[4];

    sealed class Timer
    {
        public int Latch = 0xFFFF, Counter = 0xFFFF, Control;
        public bool Toggle;                // the PB6/PB7 output in toggle mode
        public long LastUnderflow = long.MinValue / 2;
        public bool Running => (Control & 1) != 0;
        public bool OneShot => (Control & 8) != 0;
    }

    protected Cia(Bus bus, int start)
    {
        Bus = bus;
        _start = start;
        _lastCycle = Now();
        _todAt = bus.Seconds();
    }

    long Now() => (long)(Bus.Seconds() * ClockHz);

    /// <summary>The bus clock in seconds as the chip sees it now.</summary>
    internal double BusSeconds => Bus.Seconds();

    /// <summary>True while an enabled interrupt source has fired; the CPU's IRQ (CIA 1) or NMI (CIA 2) line.</summary>
    public bool InterruptPending
    {
        get { Sync(); return (_flags & _mask & 0x1F) != 0; }
    }

    /// <summary>Pin levels of port A / B as driven from outside (1 = high). Undriven pins are pulled up.</summary>
    protected virtual byte PinsA() => 0xFF;
    protected virtual byte PinsB() => 0xFF;

    /// <summary>The lines this port drives low (output pins whose latch is 0).</summary>
    protected byte DrivenLowA => (byte)(~Pra & Ddra);

    /// <summary>Raised after a write to port A or its direction register (CIA 2's serial bus lines are driven from there).</summary>
    public event Action? PortAChanged;
    protected byte DrivenLowB => (byte)(~Prb & Ddrb);

    /// <summary>What the CPU side drives on port A: output bits as written, input bits high.</summary>
    public byte PortAOutput => (byte)(Pra & Ddra | ~Ddra);

    public int TimerA => Peek(_a);
    public int TimerB => Peek(_b);

    int Peek(Timer t) { Sync(); return t.Counter; }

    // ---------- timers ----------
    void Sync()
    {
        long now = Now();
        long delta = now - _lastCycle;
        _lastCycle = now;
        if (delta <= 0) return;

        long aUnderflows = 0;
        if (_a.Running && (_a.Control & 0x20) == 0) aUnderflows = Advance(_a, delta, 1);
        else if (_a.Running) { } // counting CNT pulses: none arrive
        if (aUnderflows > 0) ShiftOut(aUnderflows);

        if (_b.Running)
        {
            switch (_b.Control >> 5 & 3)
            {
                case 0: Advance(_b, delta, 2); break;
                case 2: if (aUnderflows > 0) Advance(_b, aUnderflows, 2); break;
                case 3: if (aUnderflows > 0 && CntHigh) Advance(_b, aUnderflows, 2); break;   // A underflows counted while CNT is high
            }
        }
        CheckAlarm();
    }

    // ---------- the FLAG pin ----------
    /// <summary>
    /// A falling edge on the FLAG pin from the host (the cassette read line on CIA 1, the user port on CIA 2): sets bit 4 of the
    /// interrupt register, which raises the interrupt if it is enabled.
    /// </summary>
    public void PulseFlag()
    {
        Sync();
        _flags |= 0x10;
    }

    // ---------- the CNT pin ----------
    /// <summary>The level of the CNT pin: timer B can count A's underflows only while it is high (CRB bits 6-5 = 11).</summary>
    public bool CntHigh { get; set; } = true;

    /// <summary>
    /// A rising edge on CNT from the host (the user port). Timer A counts it when its CRA bit 5 is set, timer B when CRB bits
    /// 6-5 are 01, and the shift register in input mode would clock a bit in (see <see cref="ReceiveSerial"/>).
    /// </summary>
    public void PulseCnt(int pulses = 1)
    {
        if (pulses <= 0) return;
        Sync();
        long aUnderflows = 0;
        if (_a.Running && (_a.Control & 0x20) != 0) aUnderflows = Advance(_a, pulses, 1);
        if (aUnderflows > 0) ShiftOut(aUnderflows);
        if (!_b.Running) return;
        switch (_b.Control >> 5 & 3)
        {
            case 1: Advance(_b, pulses, 2); break;
            case 2: if (aUnderflows > 0) Advance(_b, aUnderflows, 2); break;
            case 3: if (aUnderflows > 0 && CntHigh) Advance(_b, aUnderflows, 2); break;
        }
    }

    // ---------- timer outputs on PB6 / PB7 ----------
    /// <summary>The level a timer drives onto its port B pin, or null when the pin belongs to the port (PBON clear).</summary>
    bool? TimerOutput(Timer t)
    {
        if ((t.Control & 2) == 0) return null;
        if ((t.Control & 4) != 0) return t.Toggle;
        return _lastCycle - t.LastUnderflow <= 1;      // a pulse lasts one clock cycle after the underflow
    }

    protected byte ApplyTimerOutputs(byte portB)
    {
        if (TimerOutput(_a) is bool a) portB = (byte)(a ? portB | 0x40 : portB & ~0x40);
        if (TimerOutput(_b) is bool b) portB = (byte)(b ? portB | 0x80 : portB & ~0x80);
        return portB;
    }

    // ---------- serial shift register ----------
    int _shiftUnderflows;
    bool _shifting;

    /// <summary>Raised with the byte when the shift register has clocked a whole byte out (SP pin).</summary>
    public event Action<byte>? SerialOut;

    /// <summary>The host delivers a byte on the SP pin: in input mode it lands in the data register and raises the interrupt.</summary>
    public void ReceiveSerial(byte value)
    {
        Sync();
        if ((_a.Control & 0x40) != 0) return;          // output mode: the pin is driven by the chip
        _sdr = value;
        _flags |= 8;
    }

    void ShiftOut(long timerAUnderflows)
    {
        if (!_shifting || (_a.Control & 0x40) == 0) return;
        _shiftUnderflows += (int)Math.Min(timerAUnderflows, 64);
        if (_shiftUnderflows < 16) return;               // two underflows per bit, eight bits per byte
        _shiftUnderflows = 0;
        _shifting = false;
        _flags |= 8;
        SerialOut?.Invoke(_sdr);
    }

    // ---------- time of day alarm ----------
    int _todChecked = -1;

    int AlarmTenths()
    {
        int hour = FromBcd(_alarm[3] & 0x1F) % 12 + ((_alarm[3] & 0x80) != 0 ? 12 : 0);
        return hour * 36000 + FromBcd(_alarm[2] & 0x7F) * 600 + FromBcd(_alarm[1] & 0x7F) * 10 + (_alarm[0] & 15);
    }

    void CheckAlarm()
    {
        int now = TodSeconds10();
        if (_todChecked < 0 || _todStopped) { _todChecked = now; return; }
        int alarm = AlarmTenths();
        int span = (now - _todChecked + 864000) % 864000;
        int offset = (alarm - _todChecked + 864000) % 864000;
        if (span > 0 && offset > 0 && offset <= span) _flags |= 4;     // the clock passed (or reached) the alarm time
        _todChecked = now;
    }

    /// <summary>Counts <paramref name="ticks"/> down; returns how many times the timer underflowed.</summary>
    long Advance(Timer t, long ticks, int flag)
    {
        long toUnderflow = t.Counter + 1L;
        if (ticks < toUnderflow) { t.Counter -= (int)ticks; return 0; }

        _flags |= (byte)flag;
        if (t.OneShot)
        {
            t.Control &= ~1;
            t.Counter = t.Latch;
            t.Toggle = !t.Toggle;
            t.LastUnderflow = _lastCycle;
            return 1;
        }
        long rest = ticks - toUnderflow, period = t.Latch + 1L;
        long count = 1 + rest / period;
        t.Counter = (int)(t.Latch - rest % period);
        if ((count & 1) != 0) t.Toggle = !t.Toggle;
        t.LastUnderflow = _lastCycle - rest % period;
        return count;
    }

    // ---------- time of day ----------
    int TodSeconds10()
    {
        double s = _todStopped ? _todBase : _todBase + Math.Max(0, Bus.Seconds() - _todAt);
        return (int)Math.Floor(s * 10 + 1e-9) % 864000;
    }

    static byte Bcd(int v) => (byte)(v / 10 << 4 | v % 10);
    static int FromBcd(int b) => (b >> 4 & 15) * 10 + (b & 15);

    byte ReadTod(int reg)
    {
        if (reg == 3 && !_todLatched) { _todLatch = TodSeconds10(); _todLatched = true; } // reading hours freezes the output
        int t = _todLatched ? _todLatch : TodSeconds10();
        byte value;
        switch (reg)
        {
            case 0: value = Bcd(t % 10); break;
            case 1: value = Bcd(t / 10 % 60); break;
            case 2: value = Bcd(t / 600 % 60); break;
            default:
                {
                    int h = t / 36000 % 24;
                    int h12 = h % 12 == 0 ? 12 : h % 12;
                    value = (byte)(Bcd(h12) | (h >= 12 ? 0x80 : 0));
                    break;
                }
        }
        if (reg == 0) _todLatched = false; // reading tenths releases the latch
        return value;
    }

    void WriteTod(int reg, byte value)
    {
        if ((Control(1) & 0x80) != 0) { _alarm[reg] = value; return; }
        int t = TodSeconds10();
        int tenths = t % 10, sec = t / 10 % 60, min = t / 600 % 60, hour = t / 36000 % 24;
        switch (reg)
        {
            case 0: tenths = value & 15; break;
            case 1: sec = FromBcd(value & 0x7F); break;
            case 2: min = FromBcd(value & 0x7F); break;
            default: hour = FromBcd(value & 0x1F) % 12 + ((value & 0x80) != 0 ? 12 : 0); break;
        }
        double seconds = hour * 3600 + min * 60 + sec + tenths / 10.0;
        _todLatched = false;
        _todBase = seconds;
        _todAt = Bus.Seconds();
        _todChecked = TodSeconds10();
        if (reg == 3) _todStopped = true;        // writing hours halts the clock...
        else if (reg == 0) _todStopped = false;  // ...until tenths are written
    }

    int Control(int timer) => timer == 0 ? _a.Control : _b.Control;

    // ---------- registers ----------
    public byte Read(int address)
    {
        int reg = (address - _start) & 15;
        switch (reg)
        {
            // an output pin that is high is only weakly driven: a joystick or key can still pull it low
            case 0: return (byte)((Pra & Ddra | ~Ddra) & PinsA());
            case 1: Sync(); return ApplyTimerOutputs((byte)((Prb & Ddrb | ~Ddrb) & PinsB()));
            case 2: return Ddra;
            case 3: return Ddrb;
            case 4: return (byte)Peek(_a);
            case 5: return (byte)(Peek(_a) >> 8);
            case 6: return (byte)Peek(_b);
            case 7: return (byte)(Peek(_b) >> 8);
            case 8: case 9: case 10: case 11: return ReadTod(reg - 8);
            case 12: return _sdr;
            case 13:
                {
                    Sync();
                    byte value = (byte)(_flags | ((_flags & _mask & 0x1F) != 0 ? 0x80 : 0));
                    _flags = 0; // reading acknowledges every source
                    return value;
                }
            case 14: Sync(); return (byte)(_a.Control & ~0x10);
            default: Sync(); return (byte)(_b.Control & ~0x10);
        }
    }

    public void Write(int address, byte value)
    {
        int reg = (address - _start) & 15;
        switch (reg)
        {
            case 0: Pra = value; PortAChanged?.Invoke(); break;
            case 1: Prb = value; break;
            case 2: Ddra = value; PortAChanged?.Invoke(); break;
            case 3: Ddrb = value; break;
            case 4: Sync(); _a.Latch = _a.Latch & 0xFF00 | value; break;
            case 5: Sync(); _a.Latch = _a.Latch & 0x00FF | value << 8; if (!_a.Running) _a.Counter = _a.Latch; break;
            case 6: Sync(); _b.Latch = _b.Latch & 0xFF00 | value; break;
            case 7: Sync(); _b.Latch = _b.Latch & 0x00FF | value << 8; if (!_b.Running) _b.Counter = _b.Latch; break;
            case 8: case 9: case 10: case 11: WriteTod(reg - 8, value); break;
            case 12: _sdr = value; Sync(); if ((_a.Control & 0x40) != 0) { _shifting = true; _shiftUnderflows = 0; } break;
            case 13:
                Sync();
                if ((value & 0x80) != 0) _mask |= (byte)(value & 0x1F); else _mask &= (byte)~(value & 0x1F);
                break;
            case 14: WriteControl(_a, value); break;
            default: WriteControl(_b, value); break;
        }
    }

    void WriteControl(Timer t, byte value)
    {
        Sync();
        if ((value & 0x10) != 0) t.Counter = t.Latch; // force load strobe
        if ((value & 1) != 0 && !t.Running) t.Toggle = true;   // starting a timer sets its toggle output high
        t.Control = value & ~0x10;
    }

    // ---------- saved state ----------
    internal void SaveState(BinaryWriter w)
    {
        Sync();
        w.Write(Pra); w.Write(Prb); w.Write(Ddra); w.Write(Ddrb);
        w.Write(_sdr); w.Write(_flags); w.Write(_mask);
        foreach (var t in new[] { _a, _b }) { w.Write(t.Latch); w.Write(t.Counter); w.Write(t.Control); }
        w.Write(TodSeconds10());
        w.Write(_todStopped);
        w.Write(_alarm);
        // state version 2: the timer outputs, the shift register and the CNT level
        w.Write(_a.Toggle); w.Write(_b.Toggle);
        w.Write(_shifting); w.Write(_shiftUnderflows);
        w.Write(CntHigh);
    }

    internal void LoadState(BinaryReader r, int version = 2)
    {
        Pra = r.ReadByte(); Prb = r.ReadByte(); Ddra = r.ReadByte(); Ddrb = r.ReadByte();
        _sdr = r.ReadByte(); _flags = r.ReadByte(); _mask = r.ReadByte();
        foreach (var t in new[] { _a, _b }) { t.Latch = r.ReadInt32(); t.Counter = r.ReadInt32(); t.Control = r.ReadInt32(); }
        _todBase = r.ReadInt32() / 10.0;
        _todStopped = r.ReadBoolean();
        Bus.ReadExact(r, 4).CopyTo(_alarm, 0);
        if (version >= 2)
        {
            _a.Toggle = r.ReadBoolean(); _b.Toggle = r.ReadBoolean();
            _shifting = r.ReadBoolean(); _shiftUnderflows = r.ReadInt32();
            CntHigh = r.ReadBoolean();
        }
        else { _a.Toggle = _b.Toggle = false; _shifting = false; _shiftUnderflows = 0; CntHigh = true; }
        _a.LastUnderflow = _b.LastUnderflow = long.MinValue / 2;
        _todAt = Bus.Seconds();   // the clocks run on from now
        _todLatched = false;
        _todChecked = -1;
        _lastCycle = Now();
    }

    /// <summary>
    /// The state of a chip that has just been powered on, for ROM mode where the real KERNAL sets everything up itself: all
    /// registers clear, both timers stopped with their latches at $FFFF, no interrupt sources enabled.
    /// </summary>
    public void PowerOn()
    {
        Pra = Prb = Ddra = Ddrb = 0;
        _sdr = _flags = _mask = 0;
        foreach (var t in new[] { _a, _b })
        {
            t.Latch = t.Counter = 0xFFFF;
            t.Control = 0;
            t.Toggle = false;
            t.LastUnderflow = long.MinValue / 2;
        }
        _shifting = false;
        _shiftUnderflows = 0;
        CntHigh = true;
        Array.Clear(_alarm);
        _todBase = 0;
        _todStopped = false;
        _todLatched = false;
        _todChecked = -1;
        _todAt = Bus.Seconds();
        _lastCycle = Now();
    }

    /// <summary>Power-on state the KERNAL leaves behind; subclasses refine it.</summary>
    protected void SetDefaults(int timerALatch, bool startTimerA, byte mask)
    {
        _a.Latch = _a.Counter = timerALatch;
        if (startTimerA) _a.Control = 0x81; // started, TOD at 50 Hz as the KERNAL leaves it
        _mask = mask;
    }
}

/// <summary>
/// CIA 1 at 56320: port A selects keyboard columns and carries joystick 2, port B reads keyboard rows and
/// joystick 1. Timer A runs at ~60 Hz as the system interrupt.
/// </summary>
public sealed class Cia1 : Cia
{
    public const int Start = 0xDC00;

    public Cia1(Bus bus) : base(bus, Start)
    {
        Pra = 0x7F;
        Ddra = 0xFF;
        SetDefaults(0x4025, true, 0x81);
    }

    protected override byte PinsA()
    {
        var input = Bus.Input;
        if (input == null) return 0xFF;
        byte pins = (byte)~input.Joystick(2);
        byte rows = DrivenLowB;
        for (int c = 0; c < 8; c++)
            if ((input.KeyColumn(c) & rows) != 0) pins &= (byte)~(1 << c);
        return pins;
    }

    protected override byte PinsB()
    {
        var input = Bus.Input;
        if (input == null) return 0xFF;
        byte pins = (byte)~input.Joystick(1);
        byte cols = DrivenLowA;
        for (int c = 0; c < 8; c++)
            if ((cols >> c & 1) != 0) pins &= (byte)~input.KeyColumn(c);
        return pins;
    }
}

/// <summary>CIA 2 at 56576: port A bits 0-1 choose the VIC bank (inverted); the serial bus lines read high.</summary>
public sealed class Cia2 : Cia
{
    public const int Start = 0xDD00;

    /// <summary>The levels the outside world holds on the user port's eight pins (PB0-PB7); undriven pins are high.</summary>
    public byte UserPortInput { get; set; } = 0xFF;

    /// <summary>What the C64 drives onto the user port: the pins set as outputs, with their latched levels (inputs read high).</summary>
    public byte UserPortOutput => (byte)(Prb & Ddrb | ~Ddrb);

    protected override byte PinsB() => UserPortInput;

    /// <summary>The serial bus (ROM mode): PA6 and PA7 read the CLK and DATA lines. Without one they read high.</summary>
    public Rom.IecBus? Iec { get; set; }

    protected override byte PinsA()
    {
        if (Iec == null) return 0xFF;
        double now = BusSeconds;
        return (byte)(0x3F | (Iec.ClkAt(now) ? 0x40 : 0) | (Iec.DataAt(now) ? 0x80 : 0));
    }

    public Cia2(Bus bus) : base(bus, Start)
    {
        Pra = 0x97;
        Ddra = 0x3F;
        SetDefaults(0xFFFF, false, 0);
    }
}
