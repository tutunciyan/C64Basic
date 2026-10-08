using System.Runtime.InteropServices;
using System.Text;
using C64Basic.Core.Machine;
using C64Basic.Core.Runtime;

namespace C64Basic.Console;

/// <summary>
/// A 40x25 C64 text screen kept in memory (screen codes + colour RAM) and painted onto the terminal with
/// ANSI true-colour escapes, surrounded by a border. The BASIC program sees it through PRINT, cursor/colour
/// control codes and POKEs to screen RAM (1024), colour RAM (55296), 214/211 (cursor), 646, 53280 and 53281.
/// </summary>
sealed class C64Screen
{
    public const int Rows = 25;
    const int PokeCols = 40; // screen RAM is always addressed as a 40-column grid
    readonly int _cols;
    const int ScreenRam = 1024, ColorRam = 55296;

    // Pepto's C64 palette
    static readonly (int R, int G, int B)[] Rgb =
    {
        (0x00, 0x00, 0x00), (0xFF, 0xFF, 0xFF), (0x68, 0x37, 0x2B), (0x70, 0xA4, 0xB2),
        (0x6F, 0x3D, 0x86), (0x58, 0x8D, 0x43), (0x35, 0x28, 0x79), (0xB8, 0xC7, 0x6F),
        (0x6F, 0x4F, 0x25), (0x43, 0x39, 0x00), (0x9A, 0x67, 0x59), (0x44, 0x44, 0x44),
        (0x6C, 0x6C, 0x6C), (0x9A, 0xD2, 0x84), (0x6C, 0x5E, 0xB5), (0x95, 0x95, 0x95),
    };

    // PETSCII colour control codes -> colour index
    static readonly Dictionary<char, int> ColorCodes = new()
    {
        ['\u0090'] = 0, ['\u0005'] = 1, ['\u001c'] = 2, ['\u009f'] = 3, ['\u009c'] = 4, ['\u001e'] = 5,
        ['\u001f'] = 6, ['\u009e'] = 7, ['\u0081'] = 8, ['\u0095'] = 9, ['\u0096'] = 10, ['\u0097'] = 11,
        ['\u0098'] = 12, ['\u0099'] = 13, ['\u009a'] = 14, ['\u009b'] = 15,
    };

    readonly byte[] _code;
    readonly byte[] _color;
    readonly StringBuilder _out = new();
    int _cx, _cy;
    bool _rvs;
    int _fg = 14;
    Bus? _bus;
    int _bg => _bus?.Vic.Background ?? 6;
    int _border => _bus?.Vic.Border ?? 14;
    int _ox, _oy, _bx, _by;    // terminal position of cell (0,0) and border thickness
    int _lastFg = -1, _lastBg = -1;

    readonly bool _fixedWidth;

    /// <param name="width">Fixed number of columns (at least 40), or 0 to use the whole terminal width.</param>
    public C64Screen(int width = 0)
    {
        EnableVirtualTerminal();
        int termWidth = 0;
        try { termWidth = System.Console.WindowWidth; } catch (IOException) { }
        _fixedWidth = width > 0;
        _cols = Math.Max(PokeCols, _fixedWidth ? width : termWidth);
        _code = new byte[Rows * _cols];
        _color = new byte[Rows * _cols];
        Array.Fill(_code, (byte)32);
        Array.Fill(_color, (byte)_fg);
        Layout();
        RepaintAll();
        Flush();
    }

    // ---------- terminal plumbing ----------
    [DllImport("kernel32.dll")] static extern IntPtr GetStdHandle(int handle);
    [DllImport("kernel32.dll")] static extern bool GetConsoleMode(IntPtr h, out uint mode);
    [DllImport("kernel32.dll")] static extern bool SetConsoleMode(IntPtr h, uint mode);

    static void EnableVirtualTerminal()
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            var h = GetStdHandle(-11);
            if (GetConsoleMode(h, out uint mode)) SetConsoleMode(h, mode | 0x0004);
        }
        catch (Exception) { }
    }

    void Layout()
    {
        int w = 0, h = 0;
        try { w = System.Console.WindowWidth; h = System.Console.WindowHeight; } catch (IOException) { }
        _by = h >= Rows + 2 ? 1 : 0;
        _oy = _by;
        // a fixed-width screen is centred with side borders, like a C64 on a monitor
        _bx = _fixedWidth && w >= _cols + 4 ? 2 : 0;
        _ox = _fixedWidth && w > _cols + 2 * _bx ? (w - _cols) / 2 : 0;
        if (_ox < _bx) _ox = _bx;
    }

    void SetColors(int fg, int bg)
    {
        if (fg != _lastFg)
        {
            var c = Rgb[fg];
            _out.Append("\u001b[38;2;").Append(c.R).Append(';').Append(c.G).Append(';').Append(c.B).Append('m');
            _lastFg = fg;
        }
        if (bg != _lastBg)
        {
            var c = Rgb[bg];
            _out.Append("\u001b[48;2;").Append(c.R).Append(';').Append(c.G).Append(';').Append(c.B).Append('m');
            _lastBg = bg;
        }
    }

    void MoveTo(int termCol, int termRow) => _out.Append("\u001b[").Append(termRow + 1).Append(';').Append(termCol + 1).Append('H');

    void PaintCell(int idx)
    {
        int code = _code[idx];
        int fg = _color[idx], bg = _bg;
        if (code >= 128) (fg, bg) = (bg, fg);
        MoveTo(_ox + idx % _cols, _oy + idx / _cols);
        SetColors(fg, bg);
        _out.Append(Petscii.ScreenGlyph(code));
    }

    void RepaintAll()
    {
        _lastFg = _lastBg = -1;
        SetColors(_border, _border);
        _out.Append("\u001b[2J");
        // border area: paint the frame around the text area
        int left = _ox - _bx, top = _oy - _by;
        var blank = new string(' ', _cols + 2 * _bx);
        for (int r = 0; r < Rows + 2 * _by; r++)
        {
            MoveTo(left, top + r);
            if (r < _by || r >= Rows + _by) _out.Append(blank);
            else
            {
                _out.Append(new string(' ', _bx));
                MoveTo(_ox + _cols, top + r);
                _out.Append(new string(' ', _bx));
            }
        }
        for (int r = 0; r < Rows; r++)
        {
            MoveTo(_ox, _oy + r);
            for (int c = 0; c < _cols; c++)
            {
                int idx = r * _cols + c, code = _code[idx];
                int fg = _color[idx], bg = _bg;
                if (code >= 128) (fg, bg) = (bg, fg);
                SetColors(fg, bg);
                _out.Append(Petscii.ScreenGlyph(code));
            }
        }
    }

    /// <summary>Writes the batched escapes and parks the terminal cursor on the emulated cursor.</summary>
    void Flush()
    {
        MoveTo(_ox + _cx, _oy + _cy);
        SetColors(_fg, _bg);
        try { System.Console.Out.Write(_out.ToString()); System.Console.Out.Flush(); } catch (IOException) { }
        _out.Clear();
    }

    /// <summary>Leaves the terminal usable: normal colours, cursor below the screen.</summary>
    public void Close()
    {
        _out.Clear();
        MoveTo(0, _oy + Rows + _by);
        _out.Append("\u001b[0m\r\n");
        try { System.Console.Out.Write(_out.ToString()); System.Console.Out.Flush(); } catch (IOException) { }
    }

    // ---------- text output ----------
    public static int ToScreenCode(char c)
    {
        if (Petscii.IsGlyph(c))
        {
            int p = c - Petscii.Base;
            if (p <= 127) return p - 32;
            if (p <= 191) return p - 64;
            if (p <= 254) return p - 128;
            return 94;
        }
        if (c >= 'a' && c <= 'z') return c - 'a' + 1;
        if (c >= 'A' && c <= 'Z') return c - 'A' + 1;
        return c switch
        {
            '@' => 0, '[' => 27, '£' => 28, ']' => 29, '^' => 30, '_' => 31, '|' => 93,
            >= ' ' and <= '?' => c,
            _ => 63,
        };
    }

    public void Write(string text)
    {
        foreach (char c in text)
        {
            switch (c)
            {
                case '\n': case '\r': _rvs = false; NewLine(); break;
                case '\u0093': Clear(); break;
                case '\u0013': _cx = _cy = 0; break;
                case '\u0011': if (_cy < Rows - 1) _cy++; else Scroll(); break;
                case '\u0091': if (_cy > 0) _cy--; break;
                case '\u001d': if (_cx < _cols - 1) _cx++; else if (_cy < Rows - 1) { _cx = 0; _cy++; } break;
                case '\u009d': if (_cx > 0) _cx--; else if (_cy > 0) { _cx = _cols - 1; _cy--; } break;
                case '\u0012': _rvs = true; break;
                case '\u0092': _rvs = false; break;
                case '\u0014': Backspace(); break;
                default:
                    if (ColorCodes.TryGetValue(c, out int col)) _fg = col;
                    else if (c >= ' ' && !(c >= '\u0080' && c <= '\u009f')) PutCode(ToScreenCode(c));
                    break;
            }
        }
        SyncRam();
        Flush();
    }

    void PutCode(int code)
    {
        int idx = _cy * _cols + _cx;
        _code[idx] = (byte)(code + (_rvs ? 128 : 0));
        _color[idx] = (byte)_fg;
        PaintCell(idx);
        if (++_cx >= _cols) NewLine();
    }

    void Backspace()
    {
        if (_cx == 0 && _cy == 0) return;
        if (_cx > 0) _cx--; else { _cx = _cols - 1; _cy--; }
        int idx = _cy * _cols + _cx;
        _code[idx] = 32;
        PaintCell(idx);
    }

    void NewLine()
    {
        _cx = 0;
        if (_cy < Rows - 1) _cy++; else Scroll();
    }

    void Scroll()
    {
        Array.Copy(_code, _cols, _code, 0, (Rows - 1) * _cols);
        Array.Copy(_color, _cols, _color, 0, (Rows - 1) * _cols);
        Array.Fill(_code, (byte)32, (Rows - 1) * _cols, _cols);
        Array.Fill(_color, (byte)_fg, (Rows - 1) * _cols, _cols);
        // repaint only the text area
        for (int r = 0; r < Rows; r++)
        {
            MoveTo(_ox, _oy + r);
            for (int c = 0; c < _cols; c++)
            {
                int idx = r * _cols + c, code = _code[idx];
                int fg = _color[idx], bg = _bg;
                if (code >= 128) (fg, bg) = (bg, fg);
                SetColors(fg, bg);
                _out.Append(Petscii.ScreenGlyph(code));
            }
        }
    }

    void Clear()
    {
        Array.Fill(_code, (byte)32);
        Array.Fill(_color, (byte)_fg);
        _cx = _cy = 0;
        RepaintAll();
    }

    // ---------- bus ----------
    /// <summary>Connects the screen to the memory map: POKEs reach it, and what it shows is mirrored into RAM.</summary>
    public void Attach(Bus bus)
    {
        _bus = bus;
        bus.Written += OnWritten;
        SyncRam();
        RepaintAll();
        Flush();
    }

    /// <summary>Copies screen codes, colours, the cursor and the text colour into bus RAM so PEEK sees them.</summary>
    void SyncRam()
    {
        if (_bus == null) return;
        for (int r = 0; r < Rows; r++)
        {
            Array.Copy(_code, r * _cols, _bus.Ram, ScreenRam + r * PokeCols, PokeCols);
            Array.Copy(_color, r * _cols, _bus.Color.Data, r * PokeCols, PokeCols);
        }
        _bus.Ram[214] = (byte)_cy;
        _bus.Ram[211] = (byte)Math.Min(_cx, 255);
        _bus.Ram[646] = (byte)_fg;
    }

    /// <summary>Maps an offset in the 40-column RAM grid to a cell of the (possibly wider) display.</summary>
    int Cell(int offset) => offset / PokeCols * _cols + offset % PokeCols;

    void OnWritten(int address, byte value, bool io)
    {
        if (address >= ScreenRam && address < ScreenRam + Rows * PokeCols)
        {
            int idx = Cell(address - ScreenRam);
            _code[idx] = value;
            PaintCell(idx);
        }
        else if (io && address >= ColorRam && address < ColorRam + Rows * PokeCols)
        {
            int idx = Cell(address - ColorRam);
            _color[idx] = (byte)(value & 15);
            PaintCell(idx);
        }
        else if (io && address >= 0xD000 && address < 0xD400)
        {
            int reg = Vic2.RegisterOf(address);
            if (reg != Vic2.BorderRegister && reg != Vic2.BackgroundRegister) return;
            RepaintAll();
        }
        else
        {
            switch (address)
            {
                case 214: _cy = Math.Min((int)value, Rows - 1); break;
                case 211: _cx = Math.Min((int)value, _cols - 1); break;
                case 646: _fg = value & 15; break;
                default: return;
            }
        }
        Flush();
    }

    // ---------- keyboard ----------
    /// <summary>Reads a line with echo on the emulated screen. Returns null if input ends.</summary>
    public string? ReadLine()
    {
        var sb = new StringBuilder();
        while (true)
        {
            ConsoleKeyInfo key;
            try { key = System.Console.ReadKey(true); }
            catch (InvalidOperationException) { return null; }

            if (key.Key == ConsoleKey.Enter)
            {
                Write("\n");
                return sb.ToString();
            }
            if (key.Key == ConsoleKey.Backspace)
            {
                if (sb.Length > 0) { sb.Length--; Backspace(); Flush(); }
            }
            else if (key.KeyChar >= ' ' && key.KeyChar < '\u007f')
            {
                sb.Append(key.KeyChar);
                Write(key.KeyChar.ToString());
            }
        }
    }
}
