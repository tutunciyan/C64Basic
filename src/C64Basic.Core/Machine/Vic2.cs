namespace C64Basic.Core.Machine;

/// <summary>
/// The VIC-II register file, 53248-53294, mirrored every 64 bytes through 53248-54271: sprites, graphics
/// mode bits, raster counter and compare, interrupt flags and collision latches. Drawing is in
/// <c>Vic2.Render.cs</c>.
/// </summary>
public sealed partial class Vic2 : IMemoryMapped
{
    public const int Start = 0xD000, Length = 0x400;
    public const int RegisterCount = 0x2F;

    public const int SpriteMsbX = 0x10, Control1 = 0x11, RasterLine = 0x12, SpriteEnable = 0x15, Control2 = 0x16,
        SpriteExpandY = 0x17, MemoryPointers = 0x18, IrqFlags = 0x19, IrqEnable = 0x1A, SpritePriority = 0x1B,
        SpriteMulticolor = 0x1C, SpriteExpandX = 0x1D, SpriteSpriteCollision = 0x1E, SpriteBackgroundCollision = 0x1F,
        BorderRegister = 0x20, BackgroundRegister = 0x21, SpriteMulticolor0 = 0x25, SpriteMulticolor1 = 0x26,
        SpriteColor0 = 0x27;

    /// <summary>PAL (6569): 312 raster lines of 63 CPU cycles, 19656 cycles per frame.</summary>
    public const int RasterLines = 312, CyclesPerLine = 63, CyclesPerFrame = RasterLines * CyclesPerLine;

    /// <summary>985248 / 19656, a little over 50 frames per second.</summary>
    public static readonly double FramesPerSecond = Cia.ClockHz / CyclesPerFrame;

    /// <summary>The time (in seconds on the bus clock) at which the beam is at a raster line and cycle (1-63) of the first frame.</summary>
    public static double SecondsAt(int line, int cycle = 1) => (line * (long)CyclesPerLine + cycle - 1 + 0.5) / Cia.ClockHz;

    readonly Bus _bus;
    readonly byte[] _reg = new byte[RegisterCount];
    int _rasterCompare, _bank;
    long _lastLine;
    byte _spriteSprite, _spriteBackground;

    /// <summary>The bus clock; the raster counter runs from it.</summary>
    public Func<double> Seconds
    {
        get => _bus.Seconds;
        set => _bus.Seconds = value;
    }

    public Vic2(Bus bus)
    {
        _bus = bus;
        _reg[Control1] = 0x1B;
        _reg[Control2] = 0xC8;
        _reg[MemoryPointers] = 0x15;
        _reg[BorderRegister] = 14;
        _reg[BackgroundRegister] = 6;
        _reg[SpriteMulticolor0] = 4;
        _reg[SpriteMulticolor1] = 0;
        for (int n = 0; n < 8; n++) _reg[SpriteColor0 + n] = (byte)(n < 7 ? n + 1 : 12);
        _reg.CopyTo(_baseRegs, 0);
    }

    public int Border => _reg[BorderRegister] & 15;
    public int Background => _reg[BackgroundRegister] & 15;

    long CurrentCycle => (long)Math.Floor(Seconds() * Cia.ClockHz);

    /// <summary>Raster lines since power-on; the beam is on line <c>CurrentLine % 312</c> of frame <c>CurrentLine / 312</c>.</summary>
    long CurrentLine => CurrentCycle / CyclesPerLine;

    /// <summary>The raster line (0-311) the beam is on right now.</summary>
    public int Raster => (int)(CurrentLine % RasterLines);

    /// <summary>The cycle (1-63) of the raster line the beam is in right now.</summary>
    public int CycleInLine => (int)(CurrentCycle % CyclesPerLine) + 1;

    bool IsBadLine(int line) =>
        (_reg[Control1] & 0x10) != 0 && line is >= 0x30 and <= 0xF7 && (line & 7) == (_reg[Control1] & 7);

    /// <summary>The sprites (bit n = sprite n) that are drawn on a raster line, so their data is fetched on the line before.</summary>
    int SpriteMaskOnLine(int line)
    {
        int mask = 0;
        for (int n = 0; n < 8; n++)
        {
            if ((_reg[SpriteEnable] >> n & 1) == 0) continue;
            int ys = (_reg[SpriteExpandY] >> n & 1) + 1;
            int raw = line - (_reg[n * 2 + 1] + 1);
            if (raw >= 0 && raw < 21 * ys) mask |= 1 << n;
        }
        return mask;
    }

    /// <summary>
    /// How many CPU cycles the VIC-II takes from the processor between two absolute cycles: about 40 on the first cycles of a
    /// "bad line" (when it fetches the row of characters), and for every sprite shown on the next line 2 cycles at the sprite's own
    /// place in the line (sprite 0 at cycle 58, then every 2 cycles, running over into the next line), the first one 1 more because
    /// the chip pulls BA low 3 cycles ahead.
    /// </summary>
    public int StolenCycles(long from, long to)
    {
        int stolen = 0;
        for (long n = Math.Max(0, from / CyclesPerLine - 1); n <= to / CyclesPerLine; n++)   // a line before: its sprite fetches run over
        {
            long badLine = n * CyclesPerLine + 12;                    // BA goes low at cycle 12 (the CPU stops at 15)
            if (badLine > from && badLine <= to && IsBadLine((int)(n % RasterLines))) stolen += 40;
            long first = n * CyclesPerLine + 58;                      // the first sprite's data is fetched at the end of this line
            if (first + 14 <= from || first > to) continue;           // (sprite 7's fetch ends 14 cycles later)
            int mask = SpriteMaskOnLine((int)((n + 1) % RasterLines));
            bool leading = true;
            for (int k = 0; k < 8; k++)
            {
                if ((mask >> k & 1) == 0) continue;
                long at = first + 2 * k;
                if (at > from && at <= to) stolen += leading ? 3 : 2;
                leading = false;
            }
        }
        return stolen;
    }

    long _lightPenFrame = -1;

    /// <summary>
    /// A light pen strike at beam position (<paramref name="x"/> in the sprite coordinate system, 0-511; <paramref name="y"/> the raster
    /// line, 0-311). The first strike in a frame latches the position (X in units of two pixels) into $D013/$D014 and sets the
    /// light-pen interrupt flag; further strikes in the same frame are ignored, as on the chip.
    /// </summary>
    public void LightPen(int x, int y)
    {
        long frame = CurrentLine / RasterLines;
        if (frame == _lightPenFrame) return;
        _lightPenFrame = frame;
        _reg[0x13] = (byte)(x >> 1 & 0xFF);
        _reg[0x14] = (byte)(y & 0xFF);
        PollRaster();
        _reg[IrqFlags] |= 8;
    }

    /// <summary>True while an enabled VIC interrupt source (raster, collisions) has its flag set: the IRQ line.</summary>
    public bool InterruptPending
    {
        get { PollRaster(); return (_reg[IrqFlags] & _reg[IrqEnable] & 0x0F) != 0; }
    }

    /// <summary>The register an address refers to, or -1 for the unused gap at 47-63 of each 64-byte block.</summary>
    public static int RegisterOf(int address)
    {
        int r = (address - Start) & 63;
        return r < RegisterCount ? r : -1;
    }

    /// <summary>Sets the raster-compare flag if the beam passed the start of the compare line since the last look.</summary>
    void PollRaster()
    {
        long now = CurrentLine;
        if (now == _lastLine) return;
        if (now > _lastLine)
        {
            long first = _lastLine + 1;
            long span = now - first;
            bool crossed = span >= RasterLines - 1
                || (_rasterCompare - (int)(first % RasterLines) + RasterLines) % RasterLines <= span;
            if (crossed && _rasterCompare < RasterLines) _reg[IrqFlags] |= 1;
        }
        _lastLine = now;
    }

    public byte Read(int address)
    {
        int r = RegisterOf(address);
        if (r < 0) return 0xFF;
        switch (r)
        {
            case Control1:
                PollRaster();
                return (byte)((_reg[Control1] & 0x7F) | ((Raster >> 8) << 7));
            case RasterLine:
                PollRaster();
                return (byte)(Raster & 0xFF);
            case IrqFlags:
                {
                    PollRaster();
                    int flags = _reg[IrqFlags] & 0x0F;
                    return (byte)(0x70 | flags | ((flags & _reg[IrqEnable] & 0x0F) != 0 ? 0x80 : 0));
                }
            case IrqEnable: return (byte)(_reg[IrqEnable] | 0xF0);
            case Control2: return (byte)(_reg[Control2] | 0xC0);
            case MemoryPointers: return (byte)(_reg[MemoryPointers] | 1);
            case SpriteSpriteCollision:
                {
                    ComputeCollisions();
                    byte v = _spriteSprite;
                    _spriteSprite = 0;
                    return v;
                }
            case SpriteBackgroundCollision:
                {
                    ComputeCollisions();
                    byte v = _spriteBackground;
                    _spriteBackground = 0;
                    return v;
                }
        }
        // colour registers have four bits; the unused upper bits read as 1
        return r >= BorderRegister ? (byte)(_reg[r] | 0xF0) : _reg[r];
    }

    public void Write(int address, byte value)
    {
        int r = RegisterOf(address);
        if (r < 0) return;
        switch (r)
        {
            case RasterLine:
                _rasterCompare = (_rasterCompare & 0x100) | value;
                return;
            case Control1:
                _rasterCompare = (_rasterCompare & 0xFF) | ((value & 0x80) << 1);
                _reg[r] = (byte)(value & 0x7F);
                LogWrite(r, _reg[r]);
                return;
            case IrqFlags:
                _reg[IrqFlags] = (byte)(_reg[IrqFlags] & ~value & 0x0F); // writing a 1 acknowledges that flag
                return;
            case SpriteSpriteCollision:
            case SpriteBackgroundCollision:
            case 0x13:
            case 0x14:
                return; // read-only (the light pen latches)
        }
        _reg[r] = value;
        LogWrite(r, _reg[r]);
    }

    internal byte Reg(int r) => _reg[r];

    internal void SaveState(BinaryWriter w)
    {
        w.Write(_reg);
        w.Write(_rasterCompare);
        w.Write(_spriteSprite); w.Write(_spriteBackground);
    }

    internal void LoadState(BinaryReader r)
    {
        Machine.Bus.ReadExact(r, RegisterCount).CopyTo(_reg, 0);
        _rasterCompare = r.ReadInt32();
        _spriteSprite = r.ReadByte(); _spriteBackground = r.ReadByte();
        _lastLine = CurrentLine;
        _log.Clear();
        _reg.CopyTo(_baseRegs, 0);
        _baseLine = CurrentLine;
    }

    /// <summary>
    /// A byte as the VIC sees memory: 14-bit address inside the bank chosen by CIA 2, with the character
    /// ROM shown at 4096-8191 of banks 0 and 2.
    /// </summary>
    internal byte VicRead(int address14)
    {
        int bank = _bank;
        address14 &= 0x3FFF;
        if ((bank & 1) == 0 && address14 >= 0x1000 && address14 < 0x2000)
            return _bus.CharacterRom[address14 - 0x1000];
        return _bus.Ram[(bank << 14) | address14];
    }
}

/// <summary>Colour RAM, 55296-56319: one nibble per screen cell (the first 1000 are shown).</summary>
public sealed class ColorRam : IMemoryMapped
{
    public const int Start = 0xD800, Length = 1024; // 1000 cells are visible; the rest exists but is unused

    public byte[] Data { get; } = new byte[Length];

    public ColorRam() => Array.Fill(Data, (byte)14);

    public byte Read(int address) => Data[address - Start];
    public void Write(int address, byte value) => Data[address - Start] = (byte)(value & 15);
}
