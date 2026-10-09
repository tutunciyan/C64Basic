namespace C64Basic.Core.Machine;

/// <summary>
/// A RAM expansion unit (Commodore 1700, 1764 or 1750, or one of the larger clones) on the expansion port: registers at $DF00-$DF0A and
/// a block of its own RAM that a DMA engine copies to and from the C64's RAM, one byte per cycle with the processor held. A transfer
/// runs when the command register is written with the execute bit (7) set and bit 4 (no FF00 trigger) set, or, with bit 4 clear, on the
/// next write to $FF00. Transfers are C64 to REU (0), REU to C64 (1), swap (2) and verify (3).
/// </summary>
public sealed class Reu : IMemoryMapped
{
    readonly Bus _bus;
    readonly byte[] _memory;
    readonly byte[] _reg = new byte[11];
    byte _status;                       // bit 7 interrupt pending, 6 end of block, 5 verify error
    int _c64, _reu, _length;            // the working address and length registers
    bool _armed;                        // a command waits for the write to $FF00

    /// <summary>Called with the number of cycles the DMA keeps the processor from the bus.</summary>
    public Action<int>? Halt { get; set; }

    /// <param name="sizeKilobytes">128, 256, 512, 1024 ... up to 16384.</param>
    public Reu(Bus bus, int sizeKilobytes)
    {
        if (sizeKilobytes < 128 || sizeKilobytes > 16384 || (sizeKilobytes & (sizeKilobytes - 1)) != 0)
            throw new ArgumentOutOfRangeException(nameof(sizeKilobytes), "an REU has 128, 256, 512 ... 16384 KB");
        _bus = bus;
        _memory = new byte[sizeKilobytes * 1024];
        Reset();
    }

    public int SizeKilobytes => _memory.Length / 1024;

    /// <summary>The REU's own RAM (it keeps its contents through a reset of the C64).</summary>
    public byte[] Memory => _memory;

    /// <summary>The interrupt line: an enabled end-of-block or verify-error flag is set.</summary>
    public bool IrqPending => (_status & 0x80) != 0;

    public void Reset()
    {
        Array.Clear(_reg);
        _reg[8] = 0xFF; _reg[7] = 0xFF;                   // length: 65535 at power-on
        _c64 = 0; _reu = 0; _length = 0xFFFF;
        _status = 0;
        _armed = false;
    }

    /// <summary>The chip version bit of the status register: set on a 1764 or bigger (256 KB and more).</summary>
    byte VersionBit => (byte)(_memory.Length >= 256 * 1024 ? 0x10 : 0);

    public byte Read(int address)
    {
        int r = address & 0xFF;
        if (r > 0x0A) return 0xFF;
        switch (r)
        {
            case 0:
                {
                    byte value = (byte)(_status | VersionBit);
                    _status &= 0x1F;                         // reading clears the flags
                    return value;
                }
            case 1: return (byte)(_reg[1] | 0x00);
            case 2: return (byte)_c64;
            case 3: return (byte)(_c64 >> 8);
            case 4: return (byte)_reu;
            case 5: return (byte)(_reu >> 8);
            case 6: return (byte)(_reu >> 16 | ~((_memory.Length >> 16) - 1));
            case 7: return (byte)_length;
            case 8: return (byte)(_length >> 8);
            case 9: return (byte)(_reg[9] | 0x1F);
            default: return (byte)(_reg[10] | 0x3F);
        }
    }

    public void Write(int address, byte value)
    {
        int r = address & 0xFF;
        if (r > 0x0A || r == 0) return;
        _reg[r] = value;
        switch (r)
        {
            case 1:
                if ((value & 0x80) != 0)
                {
                    if ((value & 0x10) != 0) Execute();
                    else _armed = true;                      // waits for the write to $FF00
                }
                break;
            case 2: _c64 = _c64 & 0xFF00 | value; break;
            case 3: _c64 = _c64 & 0xFF | value << 8; break;
            case 4: _reu = _reu & 0xFFFF00 | value; break;
            case 5: _reu = _reu & 0xFF00FF | value << 8; break;
            case 6: _reu = _reu & 0xFFFF | value << 16; break;
            case 7: _length = _length & 0xFF00 | value; break;
            case 8: _length = _length & 0xFF | value << 8; break;
            case 9: UpdateInterrupt(); break;
        }
    }

    /// <summary>Told about every write the processor makes to $FF00 (the trigger of a command with bit 4 clear).</summary>
    public void WroteFF00()
    {
        if (!_armed) return;
        _armed = false;
        Execute();
    }

    void UpdateInterrupt()
    {
        bool pending = (_reg[9] & 0x80) != 0 && ((_status & 0x40) != 0 && (_reg[9] & 0x40) != 0 || (_status & 0x20) != 0 && (_reg[9] & 0x20) != 0);
        _status = (byte)(pending ? _status | 0x80 : _status & 0x7F);
    }

    int Wrap(int address) => address & (_memory.Length - 1);

    void Execute()
    {
        _armed = false;
        int command = _reg[1];
        bool autoload = (command & 0x20) != 0;
        bool fixC64 = (_reg[10] & 0x80) != 0, fixReu = (_reg[10] & 0x40) != 0;
        int saveC64 = _c64, saveReu = _reu, saveLength = _length;
        int length = _length == 0 ? 0x10000 : _length;
        int type = command & 3;
        int done = 0;
        bool verifyError = false;
        var ram = _bus.Ram;

        for (; done < length; done++)
        {
            int c = _c64 & 0xFFFF, u = Wrap(_reu);
            if (type == 0) _memory[u] = ram[c];
            else if (type == 1) ram[c] = _memory[u];
            else if (type == 2) { byte t = ram[c]; ram[c] = _memory[u]; _memory[u] = t; }
            else if (ram[c] != _memory[u]) { verifyError = true; done++; break; }
            if (!fixC64) _c64 = (_c64 + 1) & 0xFFFF;
            if (!fixReu) _reu = (_reu + 1) & 0xFFFFFF;
        }

        Halt?.Invoke(done + 4);
        if (verifyError) _status |= 0x20;
        else
        {
            _status |= 0x40;                                     // end of block
            _length = 1;
        }
        if (autoload) { _c64 = saveC64; _reu = saveReu; _length = saveLength; }
        _reg[1] = (byte)(command & 0x7F | 0x00);                 // the execute bit clears; the rest stays
        UpdateInterrupt();
    }

    public void SaveState(BinaryWriter w)
    {
        w.Write(_memory.Length); w.Write(_memory); w.Write(_reg);
        w.Write(_status); w.Write(_c64); w.Write(_reu); w.Write(_length); w.Write(_armed);
    }

    public void LoadState(BinaryReader r)
    {
        int size = r.ReadInt32();
        if (size != _memory.Length) throw new InvalidDataException($"the state has a {size / 1024} KB REU and this machine has {_memory.Length / 1024} KB");
        Bus.ReadExact(r, size).CopyTo(_memory, 0);
        Bus.ReadExact(r, _reg.Length).CopyTo(_reg, 0);
        _status = r.ReadByte(); _c64 = r.ReadInt32(); _reu = r.ReadInt32(); _length = r.ReadInt32(); _armed = r.ReadBoolean();
    }
}
