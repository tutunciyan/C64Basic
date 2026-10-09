using C64Basic.Core.Machine;

namespace C64Basic.Core.Rom;

/// <summary>
/// The 1541's own computer: a 6502 at 1 MHz with 2 KB of RAM, the 16 KB DOS ROM at $C000 (mirrored at $8000) and two 6522 VIAs,
/// VIA 1 at $1800 for the serial bus and VIA 2 at $1C00 for the disk mechanics. Addresses that decode to nothing read as the high
/// byte of the address (the last value on the bus).
/// </summary>
public sealed class Drive1541 : ILockstepMember
{
    public const double ClockHz = 1_000_000;
    public const int RamSize = 2048, RomSize = 16384;

    public Cpu6502 Cpu { get; }
    public byte[] Ram { get; } = new byte[RamSize];
    public byte[] Rom { get; }
    public Via6522 Via1 { get; } = new();
    public Via6522 Via2 { get; } = new();

    /// <summary>The stepper, motor and read channel behind VIA 2.</summary>
    public DiskMechanics Mechanics { get; }

    readonly Memory _memory;

    public Drive1541(byte[] rom)
    {
        if (rom.Length != RomSize) throw new ArgumentException($"a 1541 DOS ROM is {RomSize} bytes, not {rom.Length}", nameof(rom));
        Rom = (byte[])rom.Clone();
        _memory = new Memory(this);
        Cpu = new Cpu6502(_memory);
        Cpu.IrqLine = () => Via1.IrqActive | Via2.IrqActive;
        Via1.Clock = Via2.Clock = () => Cpu.AccessCycle;
        Mechanics = new DiskMechanics(this);
        Via2.PinsA = Mechanics.PinsA;
        Via2.PinsB = Mechanics.PinsB;
        Via2.PortBChanged += Mechanics.PortBChanged;
    }

    /// <summary>Puts a disk in the drive (or takes it out with null).</summary>
    public void InsertDisk(GcrDisk? disk) => Mechanics.Insert(disk);

    /// <summary>Power on: the VIAs are cleared and the processor starts at the reset vector in the ROM.</summary>
    public void Reset()
    {
        Via1.Reset();
        Via2.Reset();
        Cpu.Reset();
    }

    // ---------- ILockstepMember ----------
    public long Cycles => Cpu.Cycles;
    double ILockstepMember.Hz => ClockHz;

    /// <summary>One instruction (or interrupt entry) of the drive's processor.</summary>
    public void Step()
    {
        Mechanics.AdvanceTo(Cpu.Cycles);
        Cpu.StepNative();
    }

    sealed class Memory : ICpuMemory
    {
        readonly Drive1541 _d;
        public Memory(Drive1541 drive) => _d = drive;

        public int Read(int address)
        {
            address &= 0xFFFF;
            if (address >= 0x8000) return _d.Rom[address & 0x3FFF];
            if ((address & 0x1800) == 0x1800) return (address & 0x0400) == 0 ? _d.Via1.Read(address) : _d.Via2.Read(address);
            if ((address & 0x1800) == 0) return _d.Ram[address & 0x7FF];
            return address >> 8;                              // nothing there: the bus floats at the high byte
        }

        public void Write(int address, byte value)
        {
            address &= 0xFFFF;
            if (address >= 0x8000) return;                    // ROM
            if ((address & 0x1800) == 0x1800)
            {
                if ((address & 0x0400) == 0) _d.Via1.Write(address, value); else _d.Via2.Write(address, value);
            }
            else if ((address & 0x1800) == 0) _d.Ram[address & 0x7FF] = value;
        }
    }
}
