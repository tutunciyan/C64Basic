using C64Basic.Core.Runtime;

namespace C64Basic.Core.Machine;

/// <summary>A chip (or other device) that answers reads and writes in a range of the I/O area.</summary>
public interface IMemoryMapped
{
    /// <param name="address">Absolute address, 53248-57343.</param>
    byte Read(int address);
    void Write(int address, byte value);
}

/// <summary>
/// The C64 memory map: 64K RAM, the character ROM and the I/O area at 53248-57343, switched by the
/// banking bits of address 1. Addresses without a chip behave as plain RAM that is only visible while I/O is.
/// </summary>
public sealed class Bus
{
    public const int IoStart = 0xD000, IoLength = 0x1000;

    /// <summary>The 64K of RAM. Writes made through this array bypass banking and <see cref="Written"/>.</summary>
    public byte[] Ram { get; } = new byte[65536];

    public Vic2 Vic { get; }
    public Sid Sound { get; } = new();
    public ColorRam Color { get; } = new();

    readonly byte[] _io = new byte[IoLength];
    readonly IMemoryMapped?[] _chips = new IMemoryMapped?[IoLength];

    /// <summary>Raised after every <see cref="Write"/>; <c>io</c> is true when the write reached the I/O area.</summary>
    public event Action<int, byte, bool>? Written;

    public Bus()
    {
        Vic = new Vic2(this);
        Ram[0] = 0x2F;      // CPU port direction
        Ram[1] = 0x37;      // BASIC, KERNAL and I/O visible
        Ram[646] = 14;      // text colour
        _io[56334 - IoStart] = 0x81;  // CIA 1 control register A
        Map(Vic2.Start, Vic2.Length, Vic);
        Map(Sid.Start, Sid.Length, Sound);
        Map(ColorRam.Start, ColorRam.Length, Color);
        _io[0xDD00 - IoStart] = 0x97; // CIA 2 port A: VIC bank 0
    }

    /// <summary>A byte of the I/O area as stored for addresses without a chip (CIA 2's VIC bank bits live here for now).</summary>
    public byte IoByte(int address) => _chips[address - IoStart]?.Read(address) ?? _io[address - IoStart];

    public void Map(int start, int length, IMemoryMapped chip)
    {
        for (int a = start; a < start + length; a++) _chips[a - IoStart] = chip;
    }

    bool Banked => (Ram[1] & 3) != 0;
    bool IoVisible => Banked && (Ram[1] & 4) != 0;
    bool CharRomVisible => Banked && (Ram[1] & 4) == 0;

    static bool InIoArea(int address) => address >= IoStart && address < IoStart + IoLength;

    public int Read(int address)
    {
        if (InIoArea(address))
        {
            if (CharRomVisible) return CharRom.Read(address);
            if (IoVisible) return _chips[address - IoStart]?.Read(address) ?? _io[address - IoStart];
        }
        return Ram[address];
    }

    public void Write(int address, byte value)
    {
        bool io = InIoArea(address) && IoVisible;
        if (io)
        {
            var chip = _chips[address - IoStart];
            if (chip != null) chip.Write(address, value);
            else _io[address - IoStart] = value;
        }
        else Ram[address] = value; // writes always reach the RAM under ROM
        Written?.Invoke(address, value, io);
    }
}
