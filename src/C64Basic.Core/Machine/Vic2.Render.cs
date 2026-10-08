namespace C64Basic.Core.Machine;

/// <summary>
/// Draws the picture one raster line at a time. Register writes are logged with the raster line and cycle they happened at, so
/// the picture shows what the beam saw: colour bars, split screens, sprites reused down the screen, the 24-row and 38-column
/// borders and the tricks that open them. The frame shown is the last complete one (or the live registers during the first frame).
/// Screen, character and sprite memory is read when the frame is drawn.
/// </summary>
public sealed partial class Vic2
{
    public const int DisplayWidth = 320, DisplayHeight = 200;
    public const int FrameWidth = 384, FrameHeight = 272;
    const int LeftBorder = (FrameWidth - DisplayWidth) / 2, TopBorder = (FrameHeight - DisplayHeight) / 2;

    // raster line 51 is the first line of the 25-row display; frame row 0 is raster line 15
    const int FirstDisplayLine = 51, FirstFrameLine = FirstDisplayLine - TopBorder;
    // frame x = 0 is where cycle 13 starts: eight pixels per cycle, 48 cycles across the frame
    const int FirstFrameCycle = 13, FrameCycles = FrameWidth / 8;

    /// <summary>Pepto's C64 palette as opaque ARGB.</summary>
    public static readonly uint[] Palette =
    {
        0xFF000000, 0xFFFFFFFF, 0xFF68372B, 0xFF70A4B2, 0xFF6F3D86, 0xFF588D43, 0xFF352879, 0xFFB8C76F,
        0xFF6F4F25, 0xFF433900, 0xFF9A6759, 0xFF444444, 0xFF6C6C6C, 0xFF9AD284, 0xFF6C5EB5, 0xFF959595,
    };

    // ---------- the register log ----------
    readonly record struct Entry(long Line, int Cycle, byte Reg, byte Value);

    readonly List<Entry> _log = new();
    readonly byte[] _baseRegs = new byte[RegisterCount];   // the registers at the start of line _baseLine
    long _baseLine;
    readonly object _composeGate = new(); // the host renders frames while the interpreter may read collision registers

    void LogWrite(int reg, byte value)
    {
        long line = CurrentLine;
        int cycle = CycleInLine;
        lock (_composeGate)
        {
            _log.Add(new Entry(line, cycle, (byte)reg, value));
            if (_log.Count > 20000) AdvanceBase(line - 2L * RasterLines); // nobody is drawing: fold the old history into the base
        }
    }

    /// <summary>Applies every logged write before <paramref name="line"/> to the base registers and forgets it.</summary>
    void AdvanceBase(long line)
    {
        int n = 0;
        while (n < _log.Count && _log[n].Line < line)
        {
            _baseRegs[_log[n].Reg] = _log[n].Value;
            n++;
        }
        _log.RemoveRange(0, n);
        if (line > _baseLine) _baseLine = line;
    }

    // ---------- frame drawing ----------
    // per-line working storage
    readonly short[] _gfx = new short[DisplayWidth];            // colour 0-15, or 0x100 + n for background register n
    readonly bool[] _foreground = new bool[DisplayWidth];
    readonly byte[] _spriteMask = new byte[FrameWidth];          // which sprites cover this frame x
    readonly sbyte[] _winner = new sbyte[FrameWidth];
    readonly byte[] _spriteColor = new byte[FrameWidth];
    readonly int[] _borderX = new int[16];                       // where the main border switches on or off along the line
    readonly bool[] _borderState = new bool[16];
    int _borderChanges;
    readonly int[] _blockBorderColor = new int[FrameCycles];
    readonly int[][] _blockBackground = Enumerable.Range(0, FrameCycles).Select(_ => new int[4]).ToArray();

    /// <summary>Renders the frame (<see cref="FrameWidth"/> x <see cref="FrameHeight"/>, ARGB) the beam last finished.</summary>
    public void Render(uint[] frame)
    {
        if (frame.Length < FrameWidth * FrameHeight) throw new ArgumentException("frame is too small", nameof(frame));
        lock (_composeGate) DrawFrame(frame, live: false);
    }

    /// <summary>Latches sprite collisions from the current registers without producing a frame.</summary>
    void ComputeCollisions() { lock (_composeGate) DrawFrame(null, live: true); }

    void DrawFrame(uint[]? frame, bool live)
    {
        _bank = ~_bus.IoByte(0xDD00) & 3; // CIA 2 port A, read once per frame
        long frameIndex = CurrentLine / RasterLines;
        var regs = new byte[RegisterCount];
        int entry = 0;
        long start = 0;
        if (live || frameIndex == 0)
        {
            _reg.CopyTo(regs, 0);   // the first frame: nothing is complete yet, show the registers as they are
            live = true;
        }
        else
        {
            start = (frameIndex - 1) * RasterLines;
            AdvanceBase(start);
            _baseRegs.CopyTo(regs, 0);
        }

        bool verticalBorder = true, mainBorder = true;
        byte spriteSprite = 0, spriteBackground = 0;
        for (int line = 0; line < RasterLines; line++)
        {
            long absolute = start + line;
            if (!live) ApplyEntries(regs, ref entry, absolute, FirstFrameCycle);

            // the vertical border flip-flop: set at the bottom of the display, cleared at the top (if the display is on)
            int d11 = regs[Control1];
            bool rsel = (d11 & 8) != 0;
            if (line == (rsel ? 251 : 247)) verticalBorder = true;
            if (line == (rsel ? 51 : 55) && (d11 & 0x10) != 0) verticalBorder = false;
            if (verticalBorder) mainBorder = true;

            int frameRow = line - FirstFrameLine;
            bool visible = frameRow >= 0 && frameRow < FrameHeight;
            if (!visible && !(live && frame == null)) continue;

            // graphics and sprites for the line, then the border flip-flops decide what is seen
            int dy = line - FirstDisplayLine - ((d11 & 7) - 3);
            bool graphics = (d11 & 0x10) != 0 && dy >= 0 && dy < DisplayHeight;
            if (graphics) DrawGraphicsLine(dy, regs);
            DrawSpriteLine(line, regs);

            _borderChanges = 0;
            _borderX[0] = 0; _borderState[0] = mainBorder; _borderChanges = 1;
            for (int block = 0; block < FrameCycles; block++)
            {
                int cycle = FirstFrameCycle + block;
                if (!live) ApplyEntries(regs, ref entry, absolute, cycle);
                bool csel = (regs[Control2] & 8) != 0;
                // the left edge (x 32, or 39 in 38-column mode) and the right edge (x 343, or 352)
                if (!verticalBorder && mainBorder && ((cycle == 17 && csel) || (cycle == 18 && !csel)))
                    SetBorder(false, cycle == 17 ? 32 : 39, ref mainBorder);
                if (!mainBorder && ((cycle == 56 && !csel) || (cycle == 57 && csel)))
                    SetBorder(true, cycle == 56 ? 343 : 352, ref mainBorder);
                _blockBorderColor[block] = regs[BorderRegister] & 15;
                for (int k = 0; k < 4; k++) _blockBackground[block][k] = regs[BackgroundRegister + k] & 15;
            }

            int change = 0;
            for (int x = 0; x < FrameWidth; x++)
            {
                int block = x / 8, gx = x - LeftBorder;
                while (change + 1 < _borderChanges && _borderX[change + 1] <= x) change++;
                bool border = _borderState[change];
                bool inDisplay = !border && graphics && gx >= 0 && gx < DisplayWidth;
                byte mask = _spriteMask[x];
                if (!border && mask != 0)
                {
                    if ((mask & (mask - 1)) != 0) spriteSprite |= mask;                  // two or more sprites on one pixel
                    if (inDisplay && _foreground[gx]) spriteBackground |= mask;
                }

                if (frame == null || !visible) continue;
                uint pixel;
                if (border) pixel = Palette[_blockBorderColor[block]];
                else
                {
                    int color = inDisplay ? Resolve(_gfx[gx], block) : _blockBackground[block][0];
                    int w = _winner[x];
                    if (w >= 0 && !(inDisplay && _foreground[gx] && (regs[SpritePriority] >> w & 1) != 0)) color = _spriteColor[x];
                    pixel = Palette[color];
                }
                frame[frameRow * FrameWidth + x] = pixel;
            }
        }

        // collisions stay latched until read; the first one of each kind raises its interrupt flag
        if (spriteSprite != 0) { if (_spriteSprite == 0) _reg[IrqFlags] |= 4; _spriteSprite |= spriteSprite; }
        if (spriteBackground != 0) { if (_spriteBackground == 0) _reg[IrqFlags] |= 2; _spriteBackground |= spriteBackground; }
    }

    void SetBorder(bool on, int x, ref bool mainBorder)
    {
        mainBorder = on;
        if (_borderChanges < _borderX.Length) { _borderX[_borderChanges] = x; _borderState[_borderChanges] = on; _borderChanges++; }
    }

    int Resolve(short value, int block) => value >= 0x100 ? _blockBackground[block][value & 3] : value;

    /// <summary>Applies the logged writes up to a line and cycle.</summary>
    void ApplyEntries(byte[] regs, ref int entry, long line, int cycle)
    {
        while (entry < _log.Count)
        {
            var e = _log[entry];
            if (e.Line > line || (e.Line == line && e.Cycle >= cycle)) break;   // a write during a cycle shows from the next one
            regs[e.Reg] = e.Value;
            entry++;
        }
    }

    // ---------- the graphics of one display line ----------
    void DrawGraphicsLine(int dy, byte[] regs)
    {
        int d11 = regs[Control1], d16 = regs[Control2], d18 = regs[MemoryPointers];
        bool ecm = (d11 & 0x40) != 0, bmm = (d11 & 0x20) != 0, mcm = (d16 & 0x10) != 0;
        Array.Fill(_gfx, (short)0x100);
        Array.Clear(_foreground);
        if (ecm && (bmm || mcm))
        {
            Array.Fill(_gfx, (short)0); // an invalid mode shows black
            return;
        }

        int xscroll = d16 & 7;
        int screen = (d18 >> 4 & 15) * 1024, chars = (d18 >> 1 & 7) * 2048, bitmap = (d18 >> 3 & 1) * 8192;
        int cy = dy >> 3, line = dy & 7;
        for (int cx = 0; cx < 40; cx++)
        {
            int cell = cy * 40 + cx;
            int sc = VicRead(screen + cell), cr = _bus.Color.Data[cell] & 15;
            int pat = bmm ? VicRead(bitmap + cell * 8 + line) : VicRead(chars + (ecm ? sc & 63 : sc) * 8 + line);
            int o = cx * 8 + xscroll;
            bool multi = mcm && (bmm || (cr & 8) != 0);
            if (multi)
            {
                for (int p = 0; p < 4; p++)
                {
                    int bits = pat >> (6 - 2 * p) & 3;
                    int c = bmm
                        ? bits switch { 0 => 0x100, 1 => sc >> 4, 2 => sc & 15, _ => cr }
                        : bits switch { 0 => 0x100, 1 => 0x101, 2 => 0x102, _ => cr & 7 };
                    for (int k = 0; k < 2; k++) Put(o + p * 2 + k, (short)c, bits >= 2);
                }
            }
            else
            {
                int on = bmm ? sc >> 4 : mcm ? cr & 7 : cr;
                int off = bmm ? sc & 15 : ecm ? 0x100 + (sc >> 6) : 0x100;
                for (int b = 0; b < 8; b++)
                {
                    bool set = (pat >> (7 - b) & 1) != 0;
                    Put(o + b, (short)(set ? on : off), set);
                }
            }
        }
    }

    void Put(int x, short color, bool foreground)
    {
        if (x < 0 || x >= DisplayWidth) return;
        _gfx[x] = color;
        _foreground[x] = foreground;
    }

    // ---------- the sprites of one raster line ----------
    void DrawSpriteLine(int line, byte[] regs)
    {
        Array.Clear(_spriteMask);
        Array.Fill(_winner, (sbyte)-1);
        int enable = regs[SpriteEnable];
        if (enable == 0) return;
        int screen = (regs[MemoryPointers] >> 4 & 15) * 1024;

        for (int n = 0; n < 8; n++)
        {
            if ((enable >> n & 1) == 0) continue;
            int xs = (regs[SpriteExpandX] >> n & 1) + 1, ys = (regs[SpriteExpandY] >> n & 1) + 1;
            int raw = line - (regs[n * 2 + 1] + 1);              // the sprite's first line is the one below its Y
            if (raw < 0 || raw >= 21 * ys) continue;
            int row = raw / ys;

            int x = regs[n * 2] | (regs[SpriteMsbX] >> n & 1) << 8;
            bool multi = (regs[SpriteMulticolor] >> n & 1) != 0;
            int data = VicRead(screen + 1016 + n) * 64;
            int bits = VicRead(data + row * 3) << 16 | VicRead(data + row * 3 + 1) << 8 | VicRead(data + row * 3 + 2);
            int own = regs[SpriteColor0 + n] & 15;

            for (int unit = 0; unit < (multi ? 12 : 24); unit++)
            {
                int c;
                if (multi)
                    c = (bits >> (22 - 2 * unit) & 3) switch
                    {
                        1 => regs[SpriteMulticolor0] & 15,
                        2 => own,
                        3 => regs[SpriteMulticolor1] & 15,
                        _ => -1,
                    };
                else c = (bits >> (23 - unit) & 1) != 0 ? own : -1;
                if (c < 0) continue;

                int width = (multi ? 2 : 1) * xs, left = x + 8 + unit * width; // sprite x 24 is frame x 32
                for (int dx = 0; dx < width; dx++)
                {
                    int px = left + dx;
                    if (px < 0 || px >= FrameWidth) continue;
                    _spriteMask[px] |= (byte)(1 << n);
                    if (_winner[px] < 0) { _winner[px] = (sbyte)n; _spriteColor[px] = (byte)c; }
                }
            }
        }
    }
}
