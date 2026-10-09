using System.Text;
using C64Basic.Core.Machine;
using C64Basic.Core.Rom;

namespace C64Basic.Tests;

/// <summary>Finds the (copyrighted, git-ignored) ROM dumps in the repository's <c>roms</c> folder; tests that need them do nothing without.</summary>
public static class TestRoms
{
    public static RomSet? Find()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            string roms = Path.Combine(dir.FullName, "roms");
            if (!Directory.Exists(roms)) continue;
            try { return RomSet.Find(roms); }
            catch (FileNotFoundException) { return null; }
        }
        return null;
    }
}

/// <summary>
/// A stand-in for the C64 on the serial bus: it drives CIA 2's port A by hand, following the KERNAL's protocol, while the drive's
/// own processor runs the real DOS ROM. Time is the drive's clock (1 cycle = 1 microsecond).
/// </summary>
public sealed class IecHost
{
    public Bus Bus { get; } = new();
    public Drive1541 Drive { get; }
    public IecBus Iec { get; }

    byte _pra;

    public IecHost(byte[] dosRom, int device = 8)
    {
        Drive = new Drive1541(dosRom);
        Iec = new IecBus(Bus.Cia2);
        Bus.Cia2.Write(0xDD02, 0x3F);        // PA0-5 outputs, PA6-7 inputs
        SetPins(atnLow: false, clkLow: false, dataLow: false);
        Iec.Attach(Drive, device);
        Drive.Reset();
    }

    void SetPins(bool atnLow, bool clkLow, bool dataLow)
    {
        _pra = (byte)((atnLow ? 0x08 : 0) | (clkLow ? 0x10 : 0) | (dataLow ? 0x20 : 0));
        Bus.Cia2.Write(0xDD00, _pra);
    }

    bool _atnLow, _clkLow, _dataLow;
    public void Atn(bool low) { _atnLow = low; SetPins(_atnLow, _clkLow, _dataLow); }
    public void Clk(bool low) { _clkLow = low; SetPins(_atnLow, _clkLow, _dataLow); }
    public void Data(bool low) { _dataLow = low; SetPins(_atnLow, _clkLow, _dataLow); }

    public bool ClkIsHigh => Iec.Clk;
    public bool DataIsHigh => Iec.Data;

    // ---------- time ----------
    public void Run(long microseconds)
    {
        long end = Drive.Cycles + microseconds;
        while (Drive.Cycles < end) Drive.Step();
    }

    /// <summary>Runs until the condition holds; false if it did not within the time.</summary>
    public bool RunUntil(Func<bool> condition, long microseconds)
    {
        long end = Drive.Cycles + microseconds;
        while (!condition())
        {
            if (Drive.Cycles >= end) return false;
            Drive.Step();
        }
        return true;
    }

    // ---------- the protocol ----------
    /// <summary>
    /// Sends one byte as the talker; true if the listener acknowledged it. We hold CLK low and the listener holds DATA low when this
    /// starts, as they do after a command under ATN.
    /// </summary>
    public bool Send(byte value, bool eoi = false)
    {
        Run(60);
        Clk(false);                                                  // ready to send
        if (!RunUntil(() => DataIsHigh, 1_000_000)) return false;    // and the listener is ready too
        if (eoi)
        {
            // wait long enough for the listener to notice and acknowledge with a pulse on DATA
            if (!RunUntil(() => !DataIsHigh, 2000)) return false;
            if (!RunUntil(() => DataIsHigh, 2000)) return false;
        }
        else Run(30);
        for (int bit = 0; bit < 8; bit++)
        {
            Clk(true);                                               // CLK low: the bit is coming
            Data((value >> bit & 1) == 0);                           // a 1 is a released DATA line
            Run(60);
            Clk(false);                                              // CLK high: the bit is valid
            Run(60);
        }
        Clk(true);
        Data(false);
        return RunUntil(() => !DataIsHigh, 2000);                   // the frame is acknowledged by DATA low
    }

    /// <summary>Sends command bytes (LISTEN, TALK, a secondary address) under ATN. False if the device did not answer.</summary>
    public bool Command(params byte[] bytes)
    {
        Atn(true);
        Clk(true);
        Data(false);
        if (!RunUntil(() => !DataIsHigh, 1500)) { Atn(false); Clk(false); return false; }     // no device: nobody pulls DATA
        foreach (byte b in bytes)
        {
            if (!Send(b)) { Atn(false); Clk(false); return false; }
            Run(100);
        }
        return true;
    }

    /// <summary>Lets go of ATN and all the lines (after UNLISTEN or UNTALK).</summary>
    public void Release()
    {
        Atn(false); Clk(false); Data(false);
        Run(200);
    }

    /// <summary>After the last command byte of a LISTEN: ATN goes high, we stay the talker with CLK low.</summary>
    public void EndCommandAsTalker()
    {
        Atn(false);
        Run(100);
    }

    /// <summary>After TALK and its secondary address: the turn-around. We become the listener.</summary>
    public bool TurnAround()
    {
        Data(true);
        Atn(false);
        Clk(false);
        return RunUntil(() => !ClkIsHigh, 20_000);                   // the drive pulls CLK low: it is the talker now
    }

    /// <summary>Receives one byte from the talker; null on a timeout.</summary>
    public (byte Value, bool Eoi)? Receive()
    {
        if (!RunUntil(() => ClkIsHigh, 2_000_000)) return null;      // the talker is ready
        Data(false);                                                 // we are ready too
        bool eoi = false;
        if (!RunUntil(() => !ClkIsHigh, 300))
        {
            eoi = true;                                              // nothing for 200 us: this is the last byte
            Data(true); Run(80); Data(false);
            if (!RunUntil(() => !ClkIsHigh, 5000)) return null;
        }
        int value = 0;
        for (int bit = 0; bit < 8; bit++)
        {
            if (!RunUntil(() => ClkIsHigh, 5000)) return null;
            if (DataIsHigh) value |= 1 << bit;
            if (!RunUntil(() => !ClkIsHigh, 5000)) return null;
        }
        Data(true);                                                  // frame acknowledged
        Run(60);
        return ((byte)value, eoi);
    }

    public string ReceiveLine()
    {
        var sb = new StringBuilder();
        while (Receive() is { } b)
        {
            sb.Append((char)b.Value);
            if (b.Eoi || b.Value == 13) break;
        }
        return sb.ToString();
    }
}
