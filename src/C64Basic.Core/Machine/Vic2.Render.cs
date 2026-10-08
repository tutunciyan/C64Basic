namespace C64Basic.Core.Machine;

/// <summary>Draws the picture: text, multicolour text, extended colour, bitmap modes and sprites.</summary>
public sealed partial class Vic2
{
    public const int DisplayWidth = 320, DisplayHeight = 200;
    public const int FrameWidth = 384, FrameHeight = 272;
    const int LeftBorder = (FrameWidth - DisplayWidth) / 2, TopBorder = (FrameHeight - DisplayHeight) / 2;

    // sprite coordinate (24, 50) is the top-left pixel of the 320x200 display window
    const int SpriteOriginX = 24, SpriteOriginY = 50;

    /// <summary>Pepto's C64 palette as opaque ARGB.</summary>
    public static readonly uint[] Palette =
    {
        0xFF000000, 0xFFFFFFFF, 0xFF68372B, 0xFF70A4B2, 0xFF6F3D86, 0xFF588D43, 0xFF352879, 0xFFB8C76F,
        0xFF6F4F25, 0xFF433900, 0xFF9A6759, 0xFF444444, 0xFF6C6C6C, 0xFF9AD284, 0xFF6C5EB5, 0xFF959595,
    };

    readonly object _composeGate = new(); // the host renders frames while the interpreter may read collision registers
    readonly byte[] _pixel = new byte[DisplayWidth * DisplayHeight];  // colour index of the graphics layer
    readonly bool[] _foreground = new bool[DisplayWidth * DisplayHeight]; // pixel counts as foreground for priority/collision
    readonly sbyte[] _winner = new sbyte[DisplayWidth * DisplayHeight];   // highest-priority sprite on a pixel, or -1
    readonly byte[] _spriteColor = new byte[DisplayWidth * DisplayHeight];

    /// <summary>Renders the whole frame (<see cref="FrameWidth"/> x <see cref="FrameHeight"/>, ARGB) from current memory.</summary>
    public void Render(uint[] frame)
    {
        if (frame.Length < FrameWidth * FrameHeight) throw new ArgumentException("frame is too small", nameof(frame));
        lock (_composeGate) RenderLocked(frame);
    }

    void RenderLocked(uint[] frame)
    {
        Compose();

        uint border = Palette[Border];
        Array.Fill(frame, border, 0, FrameWidth * FrameHeight);

        int d16 = _reg[Control2], d11 = _reg[Control1];
        int x0 = (d16 & 8) != 0 ? 0 : 7, x1 = (d16 & 8) != 0 ? DisplayWidth : DisplayWidth - 9;
        int y0 = (d11 & 8) != 0 ? 0 : 4, y1 = (d11 & 8) != 0 ? DisplayHeight : DisplayHeight - 4;
        for (int y = y0; y < y1; y++)
        {
            int row = (y + TopBorder) * FrameWidth + LeftBorder;
            for (int x = x0; x < x1; x++)
            {
                int i = y * DisplayWidth + x;
                int s = _winner[i];
                int color = s >= 0 && !(_foreground[i] && (_reg[SpritePriority] >> s & 1) != 0)
                    ? _spriteColor[i]
                    : _pixel[i];
                frame[row + x] = Palette[color];
            }
        }
    }

    /// <summary>Latches sprite collisions from current memory without producing a frame.</summary>
    void ComputeCollisions() { lock (_composeGate) Compose(); }

    void Compose()
    {
        _bank = ~_bus.IoByte(0xDD00) & 3; // CIA 2 port A, read once per frame
        DrawGraphics();
        DrawSprites();
    }

    void DrawGraphics()
    {
        int d11 = _reg[Control1], d16 = _reg[Control2], d18 = _reg[MemoryPointers];
        bool ecm = (d11 & 0x40) != 0, bmm = (d11 & 0x20) != 0, mcm = (d16 & 0x10) != 0;
        var bg = new[] { _reg[0x21] & 15, _reg[0x22] & 15, _reg[0x23] & 15, _reg[0x24] & 15 };
        Array.Clear(_foreground);

        if ((d11 & 0x10) == 0 || (ecm && (bmm || mcm)))
        {
            // display off, or an invalid mode: the display shows the border colour / black
            Array.Fill(_pixel, (byte)((d11 & 0x10) == 0 ? Border : 0));
            return;
        }

        int screen = (d18 >> 4 & 15) * 1024, chars = (d18 >> 1 & 7) * 2048, bitmap = (d18 >> 3 & 1) * 8192;
        for (int cy = 0; cy < 25; cy++)
        {
            for (int cx = 0; cx < 40; cx++)
            {
                int cell = cy * 40 + cx;
                int sc = VicRead(screen + cell), cr = _bus.Color.Data[cell] & 15;
                for (int line = 0; line < 8; line++)
                {
                    int pat = bmm ? VicRead(bitmap + cell * 8 + line) : VicRead(chars + (ecm ? sc & 63 : sc) * 8 + line);
                    int o = (cy * 8 + line) * DisplayWidth + cx * 8;
                    bool multi = mcm && (bmm || (cr & 8) != 0);
                    if (multi)
                    {
                        for (int p = 0; p < 4; p++)
                        {
                            int bits = pat >> (6 - 2 * p) & 3;
                            int c = bmm
                                ? bits switch { 0 => bg[0], 1 => sc >> 4, 2 => sc & 15, _ => cr }
                                : bits switch { 0 => bg[0], 1 => bg[1], 2 => bg[2], _ => cr & 7 };
                            bool fg = bits >= 2;
                            for (int k = 0; k < 2; k++) { _pixel[o + p * 2 + k] = (byte)c; _foreground[o + p * 2 + k] = fg; }
                        }
                    }
                    else
                    {
                        int on = bmm ? sc >> 4 : mcm ? cr & 7 : cr;
                        int off = bmm ? sc & 15 : ecm ? bg[sc >> 6] : bg[0];
                        for (int b = 0; b < 8; b++)
                        {
                            bool set = (pat >> (7 - b) & 1) != 0;
                            _pixel[o + b] = (byte)(set ? on : off);
                            _foreground[o + b] = set;
                        }
                    }
                }
            }
        }
    }

    void DrawSprites()
    {
        Array.Fill(_winner, (sbyte)-1);
        byte sprites = 0, background = 0;
        int enable = _reg[SpriteEnable];
        int screen = (_reg[MemoryPointers] >> 4 & 15) * 1024;

        for (int n = 0; n < 8; n++)
        {
            if ((enable >> n & 1) == 0) continue;
            int x = _reg[n * 2] | (_reg[SpriteMsbX] >> n & 1) << 8;
            int y = _reg[n * 2 + 1];
            int xs = (_reg[SpriteExpandX] >> n & 1) + 1, ys = (_reg[SpriteExpandY] >> n & 1) + 1;
            bool multi = (_reg[SpriteMulticolor] >> n & 1) != 0;
            int data = VicRead(screen + 1016 + n) * 64;
            int own = _reg[SpriteColor0 + n] & 15;

            for (int row = 0; row < 21; row++)
            {
                int bits = VicRead(data + row * 3) << 16 | VicRead(data + row * 3 + 1) << 8 | VicRead(data + row * 3 + 2);
                for (int unit = 0; unit < (multi ? 12 : 24); unit++)
                {
                    int c; // colour index of this sprite pixel, or -1 when transparent
                    if (multi)
                        c = (bits >> (22 - 2 * unit) & 3) switch
                        {
                            1 => _reg[SpriteMulticolor0] & 15,
                            2 => own,
                            3 => _reg[SpriteMulticolor1] & 15,
                            _ => -1,
                        };
                    else c = (bits >> (23 - unit) & 1) != 0 ? own : -1;
                    if (c < 0) continue;

                    int width = (multi ? 2 : 1) * xs, left = x - SpriteOriginX + unit * width;
                    for (int dy = 0; dy < ys; dy++)
                    {
                        int py = y - SpriteOriginY + row * ys + dy;
                        if (py < 0 || py >= DisplayHeight) continue;
                        for (int dx = 0; dx < width; dx++)
                        {
                            int px = left + dx;
                            if (px < 0 || px >= DisplayWidth) continue;
                            int i = py * DisplayWidth + px;
                            if (_foreground[i]) background |= (byte)(1 << n);
                            if (_winner[i] < 0) { _winner[i] = (sbyte)n; _spriteColor[i] = (byte)c; }
                            else sprites |= (byte)(1 << n | 1 << _winner[i]);
                        }
                    }
                }
            }
        }

        // collisions stay latched until read; the first one of each kind raises its interrupt flag
        if (sprites != 0) { if (_spriteSprite == 0) _reg[IrqFlags] |= 4; _spriteSprite |= sprites; }
        if (background != 0) { if (_spriteBackground == 0) _reg[IrqFlags] |= 2; _spriteBackground |= background; }
    }
}
