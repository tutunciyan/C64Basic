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

    /// <summary>True while the RESTORE key is held: it pulls the NMI line, and with RUN/STOP it is the warm start.</summary>
    bool Restore => false;
}

/// <summary>
/// A 6526 Complex Interface Adapter: two 8-bit ports, two 16-bit timers, a time-of-day clock and an interrupt
/// register, mirrored every 16 bytes through its 256-byte window. Timers advance lazily from the bus clock when
/// a register is touched. Not modelled: the PB6/PB7 timer outputs, the serial port shift register and TOD alarm
/// interrupts.
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
    protected byte DrivenLowB => (byte)(~Prb & Ddrb);

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

        if (_b.Running)
        {
            switch (_b.Control >> 5 & 3)
            {
                case 0: Advance(_b, delta, 2); break;
                case 2: if (aUnderflows > 0) Advance(_b, aUnderflows, 2); break;
            }
        }
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
            return 1;
        }
        long rest = ticks - toUnderflow, period = t.Latch + 1L;
        long count = 1 + rest / period;
        t.Counter = (int)(t.Latch - rest % period);
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
            case 1: return (byte)((Prb & Ddrb | ~Ddrb) & PinsB());
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
            case 0: Pra = value; break;
            case 1: Prb = value; break;
            case 2: Ddra = value; break;
            case 3: Ddrb = value; break;
            case 4: Sync(); _a.Latch = _a.Latch & 0xFF00 | value; break;
            case 5: Sync(); _a.Latch = _a.Latch & 0x00FF | value << 8; if (!_a.Running) _a.Counter = _a.Latch; break;
            case 6: Sync(); _b.Latch = _b.Latch & 0xFF00 | value; break;
            case 7: Sync(); _b.Latch = _b.Latch & 0x00FF | value << 8; if (!_b.Running) _b.Counter = _b.Latch; break;
            case 8: case 9: case 10: case 11: WriteTod(reg - 8, value); break;
            case 12: _sdr = value; break;
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
        t.Control = value & ~0x10;
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

    public Cia2(Bus bus) : base(bus, Start)
    {
        Pra = 0x97;
        Ddra = 0x3F;
        SetDefaults(0xFFFF, false, 0);
    }
}
