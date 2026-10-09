using System.Diagnostics;
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
public sealed class Bus : ICpuMemory
{
    public const int IoStart = 0xD000, IoLength = 0x1000;

    /// <summary>The 64K of RAM. Writes made through this array bypass banking and <see cref="Written"/>.</summary>
    public byte[] Ram { get; } = new byte[65536];

    public Vic2 Vic { get; }
    public Sid Sound { get; } = new();
    public ColorRam Color { get; } = new();
    /// <summary>The character generator ROM (4096 bytes) the CPU reads at 53248 and the VIC-II draws from.</summary>
    public byte[] CharacterRom { get; private set; } = CharRom.CreateDefault();

    /// <summary>Replaces the built-in character set with a dump of the real 4096-byte ROM.</summary>
    public void LoadCharacterRom(byte[] rom)
    {
        if (rom.Length != CharRom.Length) throw new ArgumentException($"a character ROM is {CharRom.Length} bytes, not {rom.Length}", nameof(rom));
        CharacterRom = (byte[])rom.Clone();
    }

    public Cia1 Cia1 { get; }
    public Cia2 Cia2 { get; }

    /// <summary>Keys and joysticks, supplied by the host. Without one nothing is pressed and PEEK 197/653 read plain RAM.</summary>
    public IInputDevice? Input { get; set; }

    /// <summary>The CPU's IRQ line: CIA 1 (system timer) or the VIC-II.</summary>
    public bool IrqLine => Cia1.InterruptPending || Vic.InterruptPending;

    /// <summary>The CPU's NMI line: CIA 2, or the RESTORE key. The CPU reacts to the line going active.</summary>
    public bool NmiLine => Cia2.InterruptPending || (Input?.Restore ?? false);

    readonly Stopwatch _clock = Stopwatch.StartNew();

    Func<double> _seconds;
    double _offset, _cycleOrigin;
    Func<long>? _cycleSource;

    /// <summary>True while <see cref="Seconds"/> is the built-in clock (tests and hosts may replace it with their own).</summary>
    public bool ClockIsDefault { get; private set; } = true;

    /// <summary>Seconds since power-on. Raster, CIA timers, time of day and TI all run from it; tests replace it.</summary>
    public Func<double> Seconds
    {
        get => _seconds;
        set { _seconds = value; ClockIsDefault = false; }
    }

    double DefaultSeconds() =>
        _cycleSource != null ? _cycleOrigin + _cycleSource() / Cia.ClockHz : _clock.Elapsed.TotalSeconds + _offset;

    /// <summary>
    /// While machine code runs the built-in clock follows the CPU's cycle count instead of the wall clock, so the raster beam, the
    /// CIA timers and the interrupts are exactly in step with the instructions. Time stays continuous. Does nothing if the host
    /// has replaced the clock.
    /// </summary>
    internal void FollowCycles(Func<long> cycles)
    {
        if (!ClockIsDefault || _cycleSource != null) return;
        double now = DefaultSeconds();
        _cycleOrigin = now - cycles() / Cia.ClockHz;
        _cycleSource = cycles;
    }

    internal void ReleaseCycles()
    {
        if (_cycleSource == null) return;
        double now = DefaultSeconds();
        _cycleSource = null;
        _offset = now - _clock.Elapsed.TotalSeconds;
    }

    public bool FollowingCycles => _cycleSource != null;

    /// <summary>The absolute cycle (as the VIC-II counts them) at which the CPU's cycle counter has a given value.</summary>
    internal long AbsoluteCycleOf(long cpuCycles) => (long)Math.Floor(_cycleOrigin * Cia.ClockHz) + cpuCycles;

    readonly byte[] _io = new byte[IoLength];
    readonly IMemoryMapped?[] _chips = new IMemoryMapped?[IoLength];

    /// <summary>Raised after every <see cref="Write"/>; <c>io</c> is true when the write reached the I/O area.</summary>
    public event Action<int, byte, bool>? Written;

    public Bus()
    {
        _seconds = DefaultSeconds;
        Vic = new Vic2(this);
        Cia1 = new Cia1(this);
        Cia2 = new Cia2(this);
        Sound.Pot = ReadPot;
        Ram[0] = 0x2F;      // CPU port direction
        Ram[1] = 0x37;      // BASIC, KERNAL and I/O visible
        Ram[646] = 14;      // text colour
        InitialiseZeroPageAndVectors();
        Map(Vic2.Start, Vic2.Length, Vic);
        Map(Sid.Start, Sid.Length, Sound);
        Map(ColorRam.Start, ColorRam.Length, Color);
        Map(Cia1.Start, Cia.Length, Cia1);
        Map(Cia2.Start, Cia.Length, Cia2);
    }

    /// <summary>
    /// The paddle the SID sees: CIA 1 port A bits 7-6 pick the game port whose paddles are connected to POTX/POTY (01 = port 1,
    /// 10 = port 2); with neither or both selected nothing is read.
    /// </summary>
    int ReadPot(int axis) => (Cia1.PortAOutput >> 6 & 3) switch
    {
        1 => Input?.Paddle(1, axis) ?? 0,
        2 => Input?.Paddle(2, axis) ?? 0,
        _ => 0,
    };

    /// <summary>The RAM vectors and BASIC pointers the KERNAL leaves after power-on, as machine code expects them.</summary>
    void InitialiseZeroPageAndVectors()
    {
        void Word(int address, int value) { Ram[address] = (byte)value; Ram[address + 1] = (byte)(value >> 8); }
        Word(43, 0x0801);   // start of BASIC
        Word(51, 0xA000);   // bottom of strings
        Word(53, 0xA000);   // top of strings
        Word(55, 0xA000);   // top of BASIC memory
        Word(0x314, 0xEA31); // IRQ
        Word(0x316, 0xFE66); // BRK
        Word(0x318, 0xFE47); // NMI
        Ram[785] = 0x4C;    // USR: JMP to "illegal quantity"
        Word(786, 0xB248);
    }

    /// <summary>A byte of the I/O area as stored for addresses without a chip (the VIC bank bits come from CIA 2 through this).</summary>
    public byte IoByte(int address) => _chips[address - IoStart]?.Read(address) ?? _io[address - IoStart];

    /// <summary>Writes the whole machine: RAM, colour RAM, unmapped I/O storage and the VIC-II, SID and both CIAs.</summary>
    public void SaveState(BinaryWriter w)
    {
        w.Write(Ram);
        w.Write(Color.Data);
        w.Write(_io);
        Vic.SaveState(w);
        Sound.SaveState(w);
        Cia1.SaveState(w);
        Cia2.SaveState(w);
    }

    /// <summary>Reads what <see cref="SaveState"/> wrote; <paramref name="version"/> is the file's state version (1 predates the CIA extras).</summary>
    public void LoadState(BinaryReader r, int version = 2)
    {
        ReadInto(r, Ram);
        ReadInto(r, Color.Data);
        ReadInto(r, _io);
        Vic.LoadState(r);
        Sound.LoadState(r);
        Cia1.LoadState(r, version);
        Cia2.LoadState(r, version);
    }

    static void ReadInto(BinaryReader r, byte[] target) => ReadExact(r, target.Length).CopyTo(target, 0);

    /// <summary>Reads exactly <paramref name="count"/> bytes (BinaryReader.ReadBytes silently returns fewer at the end of a stream).</summary>
    internal static byte[] ReadExact(BinaryReader r, int count)
    {
        var data = r.ReadBytes(count);
        if (data.Length != count) throw new EndOfStreamException();
        return data;
    }

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
            if (CharRomVisible) return CharacterRom[address - IoStart];
            if (IoVisible) return _chips[address - IoStart]?.Read(address) ?? _io[address - IoStart];
        }
        if (Input != null && (address == 197 || address == 653)) return Keyboard.Read(Input, address);
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
