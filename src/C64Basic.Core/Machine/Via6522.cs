namespace C64Basic.Core.Machine;

/// <summary>
/// A 6522 Versatile Interface Adapter, as the 1541 uses two of them: two 8-bit ports with direction registers, two 16-bit timers
/// (T1 one-shot or free-running, T2 one-shot), the four handshake pins CA1/CA2/CB1/CB2 with their edge interrupts, and the
/// interrupt flag and enable registers. Timers advance lazily from <see cref="Clock"/> whenever a register is touched. The shift
/// register only stores a byte (nothing in a 1541 clocks it), and T2 cannot count pulses on PB6.
/// </summary>
public sealed class Via6522 : ICpuMemory
{
    public const int Length = 16;
    public const int IrqCa2 = 1, IrqCa1 = 2, IrqShift = 4, IrqCb2 = 8, IrqCb1 = 16, IrqT2 = 32, IrqT1 = 64;

    /// <summary>The cycle count the timers follow; the owner points this at its processor's bus cycle.</summary>
    public Func<long> Clock { get; set; } = () => 0;

    /// <summary>The levels the outside world holds on the port pins (1 = high); a pin nobody drives is pulled up.</summary>
    public Func<byte> PinsA { get; set; } = () => 0xFF;
    public Func<byte> PinsB { get; set; } = () => 0xFF;

    /// <summary>Raised when an output register or direction register write may have changed what the port pins are driven to.</summary>
    public event Action? PortAChanged, PortBChanged;

    public byte Ora, Orb, Ddra, Ddrb;
    byte _ifr, _ier, _acr, _pcr, _sr;
    byte _iraLatch, _irbLatch;

    // timer 1
    int _t1Latch = 0xFFFF;
    long _t1Base;
    bool _t1Running, _t1Fired, _pb7 = true;

    // timer 2
    int _t2LatchLow;
    int _t2Start;
    long _t2Base;
    bool _t2Running;

    long _lastSync;
    bool _ca1, _ca2 = true, _cb1, _cb2 = true, _ca2Handshake = true;

    public Via6522() { }

    public byte Ifr
    {
        get { bool active = IrqActive; return (byte)(_ifr & 0x7F | (active ? 0x80 : 0)); }
    }
    public byte Ier => _ier;
    public byte Acr => _acr;
    public byte Pcr => _pcr;

    /// <summary>True while an enabled interrupt flag is set (the open-collector IRQ output is pulled low).</summary>
    public bool IrqActive
    {
        get { Sync(); return (_ifr & _ier & 0x7F) != 0; }
    }

    /// <summary>The levels this chip drives on port A / B: output pins at their latched level, inputs high (nobody pulls them low).</summary>
    public byte PortAOutput => (byte)(Ora & Ddra | ~Ddra);

    public byte PortBOutput
    {
        get
        {
            byte v = (byte)(Orb & Ddrb | ~Ddrb);
            if ((_acr & 0x80) != 0 && (Ddrb & 0x80) != 0) { Sync(); v = (byte)(_pb7 ? v | 0x80 : v & 0x7F); }   // T1 drives PB7
            return v;
        }
    }

    /// <summary>The pins that are outputs: bit n set when pin n is driven by the chip.</summary>
    public byte DrivenA => Ddra;
    public byte DrivenB => Ddrb;

    /// <summary>The level of CA2 / CB2 when the chip drives them (manual high or low, handshake); inputs read as high.</summary>
    public bool Ca2Output => (_pcr >> 1 & 7) switch { 6 => false, 7 => true, 4 => _ca2Handshake, _ => true };
    public bool Cb2Output => (_pcr >> 5 & 7) switch { 6 => false, 7 => true, _ => true };

    // ---------- the handshake pins ----------
    /// <summary>The level of CA1 changes (from outside). The edge selected in PCR bit 0 sets the flag and latches port A if ACR bit 0 asks.</summary>
    public void SetCa1(bool level)
    {
        Sync();
        if (level == _ca1) return;
        _ca1 = level;
        if (level != ((_pcr & 1) != 0)) return;                 // the other edge
        _ifr |= IrqCa1;
        if ((_acr & 1) != 0) _iraLatch = PinsA();
        _ca2Handshake = true;
    }

    public void SetCb1(bool level)
    {
        Sync();
        if (level == _cb1) return;
        _cb1 = level;
        if (level != ((_pcr & 0x10) != 0)) return;
        _ifr |= IrqCb1;
        if ((_acr & 2) != 0) _irbLatch = PinsB();
    }

    /// <summary>CA2 / CB2 as inputs: negative edge (PCR 000, 001) or positive edge (010, 011) sets the flag.</summary>
    public void SetCa2(bool level)
    {
        Sync();
        if (level == _ca2) return;
        _ca2 = level;
        int mode = _pcr >> 1 & 7;
        if (mode < 4 && level == ((mode & 2) != 0)) _ifr |= IrqCa2;
    }

    public void SetCb2(bool level)
    {
        Sync();
        if (level == _cb2) return;
        _cb2 = level;
        int mode = _pcr >> 5 & 7;
        if (mode < 4 && level == ((mode & 2) != 0)) _ifr |= IrqCb2;
    }

    // ---------- timers ----------
    void Sync()
    {
        long now = Clock();
        if (now <= _lastSync) return;
        _lastSync = now;

        if (_t1Running)
        {
            long period = _t1Latch + 2L, elapsed = now - _t1Base;
            if (elapsed >= period)
            {
                if ((_acr & 0x40) != 0)
                {
                    long n = elapsed / period;
                    _ifr |= IrqT1;
                    if ((n & 1) != 0) _pb7 = !_pb7;
                    _t1Base += n * period;
                }
                else if (!_t1Fired)
                {
                    _t1Fired = true;
                    _ifr |= IrqT1;
                    _pb7 = true;
                }
            }
        }

        if (_t2Running && now - _t2Base >= _t2Start + 2L)
        {
            _t2Running = false;
            _ifr |= IrqT2;
        }
    }

    int T1Counter()
    {
        Sync();
        long elapsed = _lastSync - _t1Base;
        return (int)((_t1Latch - elapsed) & 0xFFFF);
    }

    int T2Counter()
    {
        Sync();
        return (int)((_t2Start - (_lastSync - _t2Base)) & 0xFFFF);
    }

    // ---------- registers ----------
    public int Read(int address)
    {
        int reg = address & 15;
        switch (reg)
        {
            case 0:
                Sync();
                _ifr &= unchecked((byte)~(IrqCb1 | ((_pcr >> 5 & 7) is 1 or 3 ? 0 : IrqCb2)));
                return (Orb & Ddrb) | ((PortBInput()) & ~Ddrb) & 0xFF;
            case 1: return ReadA(handshake: true);
            case 2: return Ddrb;
            case 3: return Ddra;
            case 4: { int v = T1Counter(); _ifr &= unchecked((byte)~IrqT1); return v & 0xFF; }
            case 5: return T1Counter() >> 8;
            case 6: return _t1Latch & 0xFF;
            case 7: return _t1Latch >> 8;
            case 8: { int v = T2Counter(); _ifr &= unchecked((byte)~IrqT2); return v & 0xFF; }
            case 9: return T2Counter() >> 8;
            case 10: return _sr;
            case 11: return _acr;
            case 12: return _pcr;
            case 13: return Ifr;
            case 14: return _ier | 0x80;
            default: return ReadA(handshake: false);
        }
    }

    byte PortBInput() => (byte)((_acr & 2) != 0 ? _irbLatch : PinsB());

    int ReadA(bool handshake)
    {
        Sync();
        if (handshake)
        {
            _ifr &= unchecked((byte)~(IrqCa1 | ((_pcr >> 1 & 7) is 1 or 3 ? 0 : IrqCa2)));
            if ((_pcr >> 1 & 7) == 4) _ca2Handshake = false;
        }
        byte pins = (_acr & 1) != 0 ? _iraLatch : PinsA();
        return (Ora & Ddra) | (pins & ~Ddra) & 0xFF;
    }

    public void Write(int address, byte value)
    {
        int reg = address & 15;
        switch (reg)
        {
            case 0:
                Sync();
                Orb = value;
                _ifr &= unchecked((byte)~(IrqCb1 | ((_pcr >> 5 & 7) is 1 or 3 ? 0 : IrqCb2)));
                PortBChanged?.Invoke();
                break;
            case 1:
                Sync();
                Ora = value;
                _ifr &= unchecked((byte)~(IrqCa1 | ((_pcr >> 1 & 7) is 1 or 3 ? 0 : IrqCa2)));
                if ((_pcr >> 1 & 7) == 4) _ca2Handshake = false;
                PortAChanged?.Invoke();
                break;
            case 2: Ddrb = value; PortBChanged?.Invoke(); break;
            case 3: Ddra = value; PortAChanged?.Invoke(); break;
            case 4: case 6: Sync(); _t1Latch = _t1Latch & 0xFF00 | value; break;
            case 5:
                Sync();
                _t1Latch = _t1Latch & 0xFF | value << 8;
                _t1Base = Clock();
                _lastSync = Math.Max(_lastSync, _t1Base);
                _t1Running = true;
                _t1Fired = false;
                _ifr &= unchecked((byte)~IrqT1);
                _pb7 = false;                              // PB7 (when T1 drives it) goes low, then toggles or goes high at the timeout
                PortBChanged?.Invoke();
                break;
            case 7: Sync(); _t1Latch = _t1Latch & 0xFF | value << 8; _ifr &= unchecked((byte)~IrqT1); break;
            case 8: Sync(); _t2LatchLow = value; break;
            case 9:
                Sync();
                _t2Start = _t2LatchLow | value << 8;
                _t2Base = Clock();
                _lastSync = Math.Max(_lastSync, _t2Base);
                _t2Running = true;
                _ifr &= unchecked((byte)~IrqT2);
                break;
            case 10: _sr = value; break;
            case 11: Sync(); _acr = value; PortBChanged?.Invoke(); break;
            case 12: _pcr = value; if ((_pcr >> 1 & 7) != 4) _ca2Handshake = true; break;
            case 13: _ifr &= (byte)~(value & 0x7F); break;
            case 14:
                Sync();
                if ((value & 0x80) != 0) _ier |= (byte)(value & 0x7F); else _ier &= (byte)~(value & 0x7F);
                break;
            default:
                Sync();
                Ora = value;
                PortAChanged?.Invoke();
                break;
        }
    }

    /// <summary>Power-on: every register clear, all pins inputs, no interrupts.</summary>
    public void Reset()
    {
        Ora = Orb = Ddra = Ddrb = 0;
        _ifr = _ier = _acr = _pcr = _sr = 0;
        _iraLatch = _irbLatch = 0;
        _t1Latch = 0xFFFF;
        _t1Running = _t2Running = false;
        _t1Fired = false;
        _pb7 = true;
        _ca2Handshake = true;
        _lastSync = Clock();
        PortAChanged?.Invoke();
        PortBChanged?.Invoke();
    }
}
