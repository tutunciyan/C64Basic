namespace C64Basic.Core.Disk;

/// <summary>
/// The memory a program can reach with the drive memory commands (M-R, M-W): 2 KB of RAM at $0000-$07FF, mirrored every 2 KB up
/// to $1FFF as on the 1541, and a ROM area from $C000 that reads as zero. There is no drive processor, so M-E cannot run code.
/// </summary>
public sealed class DriveMemory
{
    public const int RamSize = 0x800;

    readonly byte[] _ram = new byte[RamSize];

    public byte Read(int address)
    {
        address &= 0xFFFF;
        return address < 0x2000 ? _ram[address & (RamSize - 1)] : (byte)0;
    }

    public void Write(int address, byte value)
    {
        address &= 0xFFFF;
        if (address < 0x2000) _ram[address & (RamSize - 1)] = value;
    }
}
