namespace C64Basic.Core.Machine;

/// <summary>
/// A cartridge on the expansion port, read from a <c>.crt</c> file: ROM chips that show up at $8000 (ROML) and $A000 or $E000 (ROMH),
/// the GAME and EXROM lines that say where, and the bank registers in the I/O areas at $DE00 and $DF00. Supported hardware types: 0
/// (normal 8K, 16K and Ultimax), 5 (Ocean), 15 (C64 Game System), 19 (Magic Desk) and 32 (EasyFlash, read only: the flash cannot be
/// written). Others are refused.
/// </summary>
public sealed class Cartridge : IMemoryMapped
{
    public const int Normal = 0, Ocean = 5, GameSystem = 15, MagicDesk = 19, EasyFlash = 32;

    readonly Dictionary<(int Bank, int Region), byte[]> _chips = new();     // region 0 = ROML ($8000), 1 = ROMH ($A000 or $E000)
    readonly byte[] _ram = new byte[256];                                   // EasyFlash's RAM at $DF00
    readonly bool _initialGameLow, _initialExromLow;
    int _bank;
    int _mode;                                                              // EasyFlash's $DE02

    public string Name { get; }
    public int HardwareType { get; }

    /// <summary>The GAME line is pulled low (true) or left high.</summary>
    public bool GameLow { get; private set; }

    /// <summary>The EXROM line is pulled low (true) or left high.</summary>
    public bool ExromLow { get; private set; }

    /// <summary>GAME low and EXROM high: the cartridge takes over most of the address space (Ultimax mode).</summary>
    public bool Ultimax => GameLow && !ExromLow;

    public int Banks => _chips.Keys.Select(k => k.Bank).DefaultIfEmpty(-1).Max() + 1;

    Cartridge(string name, int type, bool gameLow, bool exromLow)
    {
        Name = name;
        HardwareType = type;
        _initialGameLow = gameLow;
        _initialExromLow = exromLow;
        Reset();
    }

    /// <summary>Parses a <c>.crt</c> file. Throws <see cref="InvalidDataException"/> for something else or a hardware type that is not supported.</summary>
    public static Cartridge FromCrt(byte[] data)
    {
        const string Signature = "C64 CARTRIDGE   ";
        if (data.Length < 0x40 || System.Text.Encoding.ASCII.GetString(data, 0, 16) != Signature) throw new InvalidDataException("not a CRT cartridge image");
        int headerLength = U32(data, 0x10);
        int type = data[0x16] << 8 | data[0x17];
        if (type is not (Normal or Ocean or GameSystem or MagicDesk or EasyFlash))
            throw new InvalidDataException($"cartridge hardware type {type} is not supported (0 normal, 5 Ocean, 15 C64 Game System, 19 Magic Desk, 32 EasyFlash)");
        bool exromLow = data[0x18] == 0, gameLow = data[0x19] == 0;
        string name = System.Text.Encoding.ASCII.GetString(data, 0x20, 32).TrimEnd('\0', ' ');
        var cart = new Cartridge(name, type, gameLow, exromLow);

        int at = Math.Max(headerLength, 0x40);
        while (at + 16 <= data.Length)
        {
            if (System.Text.Encoding.ASCII.GetString(data, at, 4) != "CHIP") throw new InvalidDataException("a CHIP packet is expected in the cartridge image");
            int packet = U32(data, at + 4);
            int bank = data[at + 10] << 8 | data[at + 11];
            int load = data[at + 12] << 8 | data[at + 13];
            int size = data[at + 14] << 8 | data[at + 15];
            if (packet < 16 || size == 0 || at + 16 + size > data.Length) throw new InvalidDataException("a CHIP packet is cut short");
            var rom = data.AsSpan(at + 16, size).ToArray();
            if (load == 0x8000 && size > 0x2000)
            {
                cart._chips[(bank, 0)] = rom[..0x2000];
                cart._chips[(bank, 1)] = rom[0x2000..];
            }
            else cart._chips[(bank, load >= 0xA000 ? 1 : 0)] = rom;
            at += packet;
        }
        if (cart._chips.Count == 0) throw new InvalidDataException("the cartridge image has no ROM chips");
        return cart;
    }

    static int U32(byte[] d, int i) => d[i] << 24 | d[i + 1] << 16 | d[i + 2] << 8 | d[i + 3];

    /// <summary>Back to what the lines and the bank register are at power-on.</summary>
    public void Reset()
    {
        _bank = 0;
        _mode = 0;
        GameLow = _initialGameLow;
        ExromLow = _initialExromLow;
        Array.Clear(_ram);
        if (HardwareType == EasyFlash) ApplyEasyFlashMode();
    }

    void ApplyEasyFlashMode()
    {
        GameLow = (_mode & 4) != 0 ? (_mode & 1) != 0 : true;      // M = 0: the boot jumper, which holds GAME low
        ExromLow = (_mode & 2) != 0;
    }

    public byte ReadRoml(int offset) => Chip(0, offset);

    public byte ReadRomh(int offset) => Chip(1, offset);

    byte Chip(int region, int offset) =>
        _chips.TryGetValue((_bank, region), out var rom) && offset < rom.Length ? rom[offset] : (byte)0xFF;

    // ---------- the I/O areas ($DE00-$DFFF) ----------
    public byte Read(int address)
    {
        if (HardwareType == EasyFlash && address >= 0xDF00) return _ram[address & 0xFF];
        if (HardwareType == GameSystem && address < 0xDF00) _bank = address & 0x3F;
        return 0xFF;
    }

    public void Write(int address, byte value)
    {
        switch (HardwareType)
        {
            case Ocean:
                if (address == 0xDE00) _bank = value & 0x3F;
                break;
            case GameSystem:
                if (address < 0xDF00) _bank = address & 0x3F;
                break;
            case MagicDesk:
                if (address == 0xDE00)
                {
                    _bank = value & 0x3F;
                    ExromLow = (value & 0x80) == 0 && _initialExromLow;      // bit 7 switches the cartridge off until the next reset
                }
                break;
            case EasyFlash:
                if (address == 0xDE00) _bank = value & 0x3F;
                else if (address == 0xDE02) { _mode = value; ApplyEasyFlashMode(); }
                else if (address >= 0xDF00) _ram[address & 0xFF] = value;
                break;
        }
    }

    public void SaveState(BinaryWriter w)
    {
        w.Write(_bank); w.Write(_mode); w.Write(GameLow); w.Write(ExromLow); w.Write(_ram);
    }

    public void LoadState(BinaryReader r)
    {
        _bank = r.ReadInt32(); _mode = r.ReadInt32(); GameLow = r.ReadBoolean(); ExromLow = r.ReadBoolean();
        Bus.ReadExact(r, _ram.Length).CopyTo(_ram, 0);
    }
}
