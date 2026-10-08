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

    /// <summary>PAL: 312 raster lines at 50 frames per second.</summary>
    public const int RasterLines = 312, FramesPerSecond = 50;

    readonly Bus _bus;
    readonly byte[] _reg = new byte[RegisterCount];
    int _rasterCompare, _lastRaster, _bank;
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
    }

    public int Border => _reg[BorderRegister] & 15;
    public int Background => _reg[BackgroundRegister] & 15;

    /// <summary>The raster line (0-311) the beam is on right now.</summary>
    public int Raster => (int)(Seconds() * FramesPerSecond * RasterLines % RasterLines);

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

    /// <summary>Sets the raster-compare flag if the beam passed the compare line since the last look.</summary>
    void PollRaster()
    {
        int now = Raster;
        if (now != _lastRaster)
        {
            bool crossed = now > _lastRaster
                ? _rasterCompare > _lastRaster && _rasterCompare <= now
                : _rasterCompare > _lastRaster || _rasterCompare <= now;
            if (crossed) _reg[IrqFlags] |= 1;
            _lastRaster = now;
        }
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
                return;
            case IrqFlags:
                _reg[IrqFlags] = (byte)(_reg[IrqFlags] & ~value & 0x0F); // writing a 1 acknowledges that flag
                return;
            case SpriteSpriteCollision:
            case SpriteBackgroundCollision:
                return; // read-only
        }
        _reg[r] = value;
    }

    internal byte Reg(int r) => _reg[r];

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
