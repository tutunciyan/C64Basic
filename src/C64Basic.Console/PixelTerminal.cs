using System.Text;
using C64Basic.Core.IO;
using C64Basic.Core.Machine;
using C64Basic.Core.Runtime;

namespace C64Basic.Console;

/// <summary>
/// Shows the real VIC-II picture in a terminal: every frame is shrunk to fit and drawn with upper half blocks in true colour, so
/// sprites, bitmap modes, custom characters and colour effects appear (text is legible when the terminal is large enough). Keys
/// feed the C64 keyboard buffer. There is no sound; use the GUI for that.
/// </summary>
sealed class PixelTerminal
{
    const int FramesPerSecond = 15;

    readonly Interpreter _interpreter;
    readonly ScreenConsole _screen;
    readonly uint[] _frame = new uint[Vic2.FrameWidth * Vic2.FrameHeight];
    uint[] _scaled = Array.Empty<uint>();
    uint[] _shown = Array.Empty<uint>();
    int _columns, _pixelRows, _termWidth, _termHeight;
    volatile bool _stop;
    Thread? _renderer, _keys;

    public PixelTerminal(Interpreter interpreter, ScreenConsole screen)
    {
        _interpreter = interpreter;
        _screen = screen;
    }

    /// <summary>True when a terminal is attached on both ends, so there is something to draw on and read from.</summary>
    public static bool Available => !System.Console.IsOutputRedirected && !System.Console.IsInputRedirected;

    public void Start()
    {
        C64Screen.EnableVirtualTerminal();
        System.Console.OutputEncoding = Encoding.UTF8;
        System.Console.CancelKeyPress += (_, e) => { e.Cancel = true; _screen.BreakRequested = true; };
        System.Console.Out.Write("\u001b[?25l\u001b[2J");   // hide the cursor, clear the screen

        _renderer = new Thread(RenderLoop) { IsBackground = true, Name = "pixel renderer" };
        _keys = new Thread(KeyLoop) { IsBackground = true, Name = "terminal keys" };
        _renderer.Start();
        _keys.Start();
    }

    public void Stop()
    {
        _stop = true;
        _renderer?.Join(500);
        try { System.Console.Out.Write("\u001b[0m\u001b[?25h\u001b[2J\u001b[H"); System.Console.Out.Flush(); }
        catch (IOException) { }
    }

    // ---------- drawing ----------
    void RenderLoop()
    {
        long blinkAt = Environment.TickCount64;
        bool blinkOn = true;
        while (!_stop)
        {
            try { Draw(ref blinkOn, ref blinkAt); }
            catch (IOException) { return; }
            Thread.Sleep(1000 / FramesPerSecond);
        }
    }

    void Draw(ref bool blinkOn, ref long blinkAt)
    {
        int width = 80, height = 25;
        try { width = System.Console.WindowWidth; height = System.Console.WindowHeight; } catch (IOException) { }
        bool resized = width != _termWidth || height != _termHeight;
        if (resized)
        {
            _termWidth = width; _termHeight = height;
            (_columns, _pixelRows) = FrameScaler.Fit(width, height, Vic2.FrameWidth, Vic2.FrameHeight);
            _scaled = new uint[_columns * _pixelRows];
            _shown = new uint[_columns * _pixelRows];
        }

        _interpreter.Bus.Vic.Render(_frame);
        if (_screen.CursorVisible)
        {
            if (Environment.TickCount64 - blinkAt >= 400) { blinkOn = !blinkOn; blinkAt = Environment.TickCount64; }
            if (blinkOn) InvertCursor();
        }
        else { blinkOn = true; blinkAt = Environment.TickCount64; }

        FrameScaler.Scale(_frame, Vic2.FrameWidth, Vic2.FrameHeight, _scaled, _columns, _pixelRows);

        var sb = new StringBuilder();
        if (resized) sb.Append("\u001b[0m\u001b[2J");
        int cellRows = _pixelRows / 2, leftMargin = Math.Max(0, (width - _columns) / 2), topMargin = Math.Max(0, (height - cellRows) / 2);
        uint lastTop = 0, lastBottom = 0;
        bool haveColours = false, atPosition = false;
        int nextRow = -1, nextColumn = -1;

        for (int r = 0; r < cellRows; r++)
        {
            for (int c = 0; c < _columns; c++)
            {
                uint top = _scaled[2 * r * _columns + c], bottom = _scaled[(2 * r + 1) * _columns + c];
                int index = 2 * r * _columns + c;
                if (!resized && _shown[index] == top && _shown[index + _columns] == bottom) { atPosition = false; continue; }
                _shown[index] = top; _shown[index + _columns] = bottom;

                if (!atPosition || nextRow != r || nextColumn != c)
                    sb.Append("\u001b[").Append(topMargin + r + 1).Append(';').Append(leftMargin + c + 1).Append('H');
                if (!haveColours || top != lastTop)
                    sb.Append("\u001b[38;2;").Append(top >> 16 & 0xFF).Append(';').Append(top >> 8 & 0xFF).Append(';').Append(top & 0xFF).Append('m');
                if (!haveColours || bottom != lastBottom)
                    sb.Append("\u001b[48;2;").Append(bottom >> 16 & 0xFF).Append(';').Append(bottom >> 8 & 0xFF).Append(';').Append(bottom & 0xFF).Append('m');
                sb.Append('▀');
                lastTop = top; lastBottom = bottom; haveColours = true;
                atPosition = true; nextRow = r; nextColumn = c + 1;
            }
        }

        if (sb.Length == 0) return;
        System.Console.Out.Write(sb.ToString());
        System.Console.Out.Flush();
    }

    /// <summary>The cursor is the character cell drawn in reverse, like in the GUI.</summary>
    void InvertCursor()
    {
        int x0 = (Vic2.FrameWidth - Vic2.DisplayWidth) / 2 + _screen.CursorColumn * 8;
        int y0 = (Vic2.FrameHeight - Vic2.DisplayHeight) / 2 + _screen.CursorRow * 8;
        for (int y = 0; y < 8; y++)
            for (int x = 0; x < 8; x++)
                _frame[(y0 + y) * Vic2.FrameWidth + x0 + x] ^= 0x00FFFFFF;
    }

    // ---------- keys ----------
    void KeyLoop()
    {
        while (!_stop)
        {
            ConsoleKeyInfo key;
            try
            {
                // polling instead of a blocking ReadKey: a thread stuck inside the console can keep the process from exiting
                ReleaseDue();
                if (!System.Console.KeyAvailable) { Thread.Sleep(8); continue; }
                key = System.Console.ReadKey(true);
            }
            catch (InvalidOperationException) { return; }
            Handle(key);
        }
    }

    void Handle(ConsoleKeyInfo key)
    {
        string? text = ConsoleKeyMap.Translate(key, out bool quit, out bool stop);
        if (quit) { _screen.Close(); return; }
        if (stop) { _screen.BreakRequested = true; Hold(new[] { 63 }); return; }
        if (text == null) return;
        _screen.Inject(text); // no ten-key limit: a terminal paste arrives as fast typing
        foreach (char c in text) Press(c);
    }

    // ---------- the key matrix ----------
    // A terminal reports a key press but never its release, so each key is held on the matrix for a short while: long enough
    // for machine code scanning $DC00/$DC01 (or PEEK 197) to see it.
    const int HoldMilliseconds = 90;

    readonly Dictionary<int, long> _held = new();

    void Press(char c)
    {
        if (KeyboardLayout.KeysFor(c) is { } keys) Hold(keys);
    }

    void Hold(int[] keys)
    {
        long until = Environment.TickCount64 + HoldMilliseconds;
        lock (_held)
            foreach (int key in keys) { _screen.SetKey(key, true); _held[key] = until; }
    }

    void ReleaseDue()
    {
        lock (_held)
        {
            if (_held.Count == 0) return;
            long now = Environment.TickCount64;
            foreach (int key in _held.Where(h => h.Value <= now).Select(h => h.Key).ToList())
            {
                _screen.SetKey(key, false);
                _held.Remove(key);
            }
        }
    }
}
