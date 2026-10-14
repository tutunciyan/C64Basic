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

    /// <summary>The 64K of RAM. Writes made through this array bypass banking.</summary>
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

    /// <summary>Keys and joysticks, supplied by the host. Without one nothing is pressed.</summary>
    public IInputDevice? Input { get; set; }

    /// <summary>The CPU's IRQ line: CIA 1 (system timer) or the VIC-II.</summary>
    public bool IrqLine => Cia1.InterruptPending || Vic.InterruptPending || (Expansion?.IrqPending ?? false);

    /// <summary>The RAM expansion unit on the expansion port, if any .</summary>
    public Reu? Expansion { get; private set; }

    /// <summary>The cartridge on the expansion port, if any .</summary>
    public Cartridge? Cart { get; private set; }

    /// <summary>Plugs a cartridge in (or pulls it out with null). The machine should be reset after.</summary>
    public void AttachCartridge(Cartridge? cart)
    {
        Cart = cart;
        for (int a = 0xDE00; a < 0xE000; a++) _chips[a - IoStart] = cart != null ? cart : a >= 0xDF00 ? Expansion : null;
        cart?.Reset();
    }

    /// <summary>Plugs a RAM expansion unit in (or pulls it out with null). It shares $DF00 with a cartridge's I/O area: the cartridge wins.</summary>
    public void AttachReu(Reu? reu)
    {
        Expansion = reu;
        for (int a = 0xDF00; a < 0xE000; a++) if (Cart == null) _chips[a - IoStart] = reu;
    }

    bool Ultimax => Cart is { Ultimax: true };

    /// <summary>The CPU's NMI line: CIA 2, or the RESTORE key. The CPU reacts to the line going active.</summary>
    public bool NmiLine => Cia2.InterruptPending || (Input?.Restore ?? false);

    readonly Stopwatch _clock = Stopwatch.StartNew();

    /// <summary>Seconds since power-on. Raster, CIA timers and time of day run from it; the machine and the tests replace it with the processor's own cycle count.</summary>
    public Func<double> Seconds { get; set; }

    readonly byte[] _io = new byte[IoLength];
    readonly IMemoryMapped?[] _chips = new IMemoryMapped?[IoLength];

    public Bus()
    {
        Seconds = () => _clock.Elapsed.TotalSeconds;
        Vic = new Vic2(this);
        Cia1 = new Cia1(this);
        Cia2 = new Cia2(this);
        Sound.Pot = ReadPot;
        Ram[0] = 0x2F;      // CPU port direction
        Ram[1] = 0x37;      // BASIC, KERNAL and I/O visible
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
        Vic.ResetPointerLog();   // after CIA 2 (the VIC bank): where the sprite pointers are
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

    // ---------- the ROMs ----------
    byte[]? _basicRom, _kernalRom;

    /// <summary>True when the real BASIC and KERNAL ROMs are in the memory map (see <see cref="EnableRomMode"/>).</summary>
    public bool RomMode => _kernalRom != null;

    /// <summary>
    /// Puts the real BASIC (8 KB at $A000) and KERNAL (8 KB at $E000) ROMs in the memory map, switched by the processor port as on the
    /// real machine, and takes the machine back to power-on: the RAM and the chips cleared, nothing set up. The processor port
    /// ($00/$01) is then also real: pins whose direction bit is 0 are inputs and read as 1, so at reset all ROMs are visible.
    /// </summary>
    public void EnableRomMode(byte[] basic, byte[] kernal)
    {
        if (basic.Length != 8192) throw new ArgumentException("the BASIC ROM is 8192 bytes", nameof(basic));
        if (kernal.Length != 8192) throw new ArgumentException("the KERNAL ROM is 8192 bytes", nameof(kernal));
        _basicRom = (byte[])basic.Clone();
        _kernalRom = (byte[])kernal.Clone();
        PowerOn();
    }

    /// <summary>RAM and chips as at power-on .</summary>
    public void PowerOn()
    {
        Array.Clear(Ram);
        Array.Clear(_io);
        Array.Fill(Color.Data, (byte)14);
        Cia1.PowerOn();
        Cia2.PowerOn();
        Cart?.Reset();
        Expansion?.Reset();
    }

    /// <summary>The three banking bits of the processor port as the memory map sees them (LORAM, HIRAM, CHAREN).</summary>
    int Port => RomMode ? (Ram[1] & Ram[0] | ~Ram[0] & 7) & 7 : Ram[1] & 7;

    /// <summary>What the processor reads at $01: output pins as written, input pins high.</summary>
    int PortRead => (Ram[1] & Ram[0] | ~Ram[0] & 0x37) & ~(CassettePlay && (Ram[0] & 0x10) == 0 ? 0x10 : 0);

    /// <summary>A datasette button is down: the sense line (bit 4 of the processor port) reads low.</summary>
    public bool CassettePlay { get; set; }

    /// <summary>The cassette motor is on: the port's bit 5 is an output and low.</summary>
    public bool CassetteMotor => RomMode && (Ram[0] & 0x20) != 0 && (Ram[1] & 0x20) == 0;

    /// <summary>The level of the cassette write line (bit 3 of the processor port).</summary>
    public bool CassetteWrite => (Ram[0] & 0x08) == 0 || (Ram[1] & 0x08) != 0;

    bool Banked => (Port & 3) != 0;
    bool IoVisible => Ultimax || Banked && (Port & 4) != 0;
    bool CharRomVisible => !Ultimax && Banked && (Port & 4) == 0;

    static bool InIoArea(int address) => address >= IoStart && address < IoStart + IoLength;

    public int Read(int address)
    {
        if (InIoArea(address))
        {
            if (CharRomVisible) return CharacterRom[address - IoStart];
            if (IoVisible) return _chips[address - IoStart]?.Read(address) ?? _io[address - IoStart];
        }
        if (_kernalRom != null)
        {
            if (Cart != null && address >= 0x8000 && address < 0xC000 | address >= 0xE000) { int v = CartRead(address); if (v >= 0) return v; }
            if (address >= 0xE000) { if ((Port & 2) != 0) return _kernalRom[address - 0xE000]; }
            else if (address >= 0xA000 && address < 0xC000) { if ((Port & 3) == 3) return _basicRom![address - 0xA000]; }
            else if (address == 1) return (byte)PortRead;
            return Ram[address];
        }
        return Ram[address];
    }

    /// <summary>
    /// What the cartridge puts on the bus at an address outside the I/O area, or -1 when it does not answer there: ROML at $8000 when its
    /// EXROM line is low and the processor port has LORAM and HIRAM set, ROMH at $A000 for a 16 KB cartridge (GAME low too) with HIRAM
    /// set, and in Ultimax mode ROML and ROMH ($E000) whatever the port says, with only $0000-$0FFF of RAM and nothing at $1000-$7FFF and $A000-$CFFF.
    /// </summary>
    int CartRead(int address)
    {
        var cart = Cart!;
        if (cart.Ultimax)
        {
            if (address >= 0xE000) return cart.ReadRomh(address & 0x1FFF);
            if (address >= 0x8000 && address < 0xA000) return cart.ReadRoml(address & 0x1FFF);
            return 0xFF;                                          // $A000-$CFFF: nothing there
        }
        if (!cart.ExromLow) return -1;
        if (address < 0xA000) return (Port & 3) == 3 ? cart.ReadRoml(address & 0x1FFF) : -1;
        if (address < 0xC000) return cart.GameLow && (Port & 2) != 0 ? cart.ReadRomh(address & 0x1FFF) : -1;
        return -1;
    }

    public void Write(int address, byte value)
    {
        if (Cart is { Ultimax: true } && address >= 0x1000 && !(address >= IoStart && address < IoStart + IoLength))
            return;                                               // no RAM there in Ultimax mode
        bool io = InIoArea(address) && IoVisible;
        if (io)
        {
            var chip = _chips[address - IoStart];
            if (chip != null) chip.Write(address, value);
            else _io[address - IoStart] = value;
            if (address >= 0xDD00 && address < 0xDE00 && (address & 0xD) == 0) Vic.PointerBaseChanged();   // CIA 2 port A or its direction: the VIC bank
        }
        else
        {
            Ram[address] = value; // writes always reach the RAM under ROM
            int pointer = address - Vic.PointerBase;
            if ((uint)pointer < 8) Vic.SpritePointerWritten(pointer, value);
        }
        if (address == 0xFF00) Expansion?.WroteFF00();
    }
}
