namespace C64Basic.Core.Machine;

/// <summary>
/// The VIC-II register file, 53248-53294, mirrored every 64 bytes through 53248-54271.
/// Only storage and the colour registers for now; sprites, modes and raster come later.
/// </summary>
public sealed class Vic2 : IMemoryMapped
{
    public const int Start = 0xD000, Length = 0x400;
    public const int RegisterCount = 0x2F, BorderRegister = 0x20, BackgroundRegister = 0x21;

    readonly byte[] _reg = new byte[RegisterCount];

    public Vic2()
    {
        _reg[BorderRegister] = 14;
        _reg[BackgroundRegister] = 6;
    }

    public int Border => _reg[BorderRegister] & 15;
    public int Background => _reg[BackgroundRegister] & 15;

    /// <summary>The register an address refers to, or -1 for the unused gap at 47-63 of each 64-byte block.</summary>
    public static int RegisterOf(int address)
    {
        int r = (address - Start) & 63;
        return r < RegisterCount ? r : -1;
    }

    public byte Read(int address)
    {
        int r = RegisterOf(address);
        if (r < 0) return 0xFF;
        // colour registers have four bits; the unused upper bits read as 1
        return r >= BorderRegister ? (byte)(_reg[r] | 0xF0) : _reg[r];
    }

    public void Write(int address, byte value)
    {
        int r = RegisterOf(address);
        if (r >= 0) _reg[r] = value;
    }
}

/// <summary>Colour RAM, 55296-56295: one nibble per screen cell.</summary>
public sealed class ColorRam : IMemoryMapped
{
    public const int Start = 0xD800, Length = 1000;

    public byte[] Data { get; } = new byte[Length];

    public ColorRam() => Array.Fill(Data, (byte)14);

    public byte Read(int address) => Data[address - Start];
    public void Write(int address, byte value) => Data[address - Start] = (byte)(value & 15);
}
