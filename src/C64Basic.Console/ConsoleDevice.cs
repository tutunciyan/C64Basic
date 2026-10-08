using System.Text;
using C64Basic.Core.IO;

namespace C64Basic.Console;

/// <summary>Maps the interpreter's screen and keyboard onto the host terminal.</summary>
sealed class ConsoleDevice : IConsoleDevice
{
    // C64 colour index -> nearest ConsoleColor
    static readonly ConsoleColor[] Palette =
    {
        ConsoleColor.Black, ConsoleColor.White, ConsoleColor.DarkRed, ConsoleColor.Cyan,
        ConsoleColor.DarkMagenta, ConsoleColor.DarkGreen, ConsoleColor.DarkBlue, ConsoleColor.Yellow,
        ConsoleColor.DarkYellow, ConsoleColor.DarkYellow, ConsoleColor.Red, ConsoleColor.DarkGray,
        ConsoleColor.Gray, ConsoleColor.Green, ConsoleColor.Blue, ConsoleColor.Gray,
    };

    // PETSCII colour control codes -> C64 colour index
    static readonly Dictionary<char, int> ColorCodes = new()
    {
        ['\u0090'] = 0, ['\u0005'] = 1, ['\u001c'] = 2, ['\u009f'] = 3, ['\u009c'] = 4, ['\u001e'] = 5,
        ['\u001f'] = 6, ['\u009e'] = 7, ['\u0081'] = 8, ['\u0095'] = 9, ['\u0096'] = 10, ['\u0097'] = 11,
        ['\u0098'] = 12, ['\u0099'] = 13, ['\u009a'] = 14, ['\u009b'] = 15,
    };

    readonly bool _colors;
    bool _reverse;

    public ConsoleDevice(bool classicColors)
    {
        _colors = classicColors && !System.Console.IsOutputRedirected;
        System.Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            BreakRequested = true;
        };
        if (_colors)
        {
            System.Console.BackgroundColor = ConsoleColor.DarkBlue;
            System.Console.ForegroundColor = ConsoleColor.Cyan;
            System.Console.Clear();
        }
    }

    public volatile bool _break;
    public bool BreakRequested { get => _break; set => _break = value; }

    static bool IsPrintable(char c) => c == (char)10 || (c >= ' ' && c < (char)0x80) || c >= (char)0xA0;

    public void Write(string text)
    {
        var o = System.Console.Out;
        var run = new StringBuilder();

        void Flush()
        {
            if (run.Length == 0) return;
            o.Write(run.ToString().Replace("\n", Environment.NewLine));
            run.Clear();
        }

        foreach (char c in text)
        {
            if (c == '\r') run.Append('\n');
            else if (IsPrintable(c)) run.Append(c);
            else
            {
                Flush();
                Control(c);
            }
        }
        Flush();
        o.Flush();
    }

    /// <summary>Handles a PETSCII control code. Ignored when output is redirected.</summary>
    void Control(char c)
    {
        if (System.Console.IsOutputRedirected) return;
        try
        {
            int x = System.Console.CursorLeft, y = System.Console.CursorTop;
            switch (c)
            {
                case '\u0093': System.Console.Clear(); break;
                case '\u0013': System.Console.SetCursorPosition(0, 0); break;
                case '\u0011': System.Console.SetCursorPosition(x, Math.Min(y + 1, System.Console.BufferHeight - 1)); break;
                case '\u0091': System.Console.SetCursorPosition(x, Math.Max(y - 1, 0)); break;
                case '\u001d': System.Console.SetCursorPosition(Math.Min(x + 1, System.Console.BufferWidth - 1), y); break;
                case '\u009d': System.Console.SetCursorPosition(Math.Max(x - 1, 0), y); break;
                case '\u0012': SetReverse(true); break;
                case '\u0092': SetReverse(false); break;
                default:
                    if (_colors && ColorCodes.TryGetValue(c, out int idx))
                    {
                        // keep reverse video consistent: the colour goes to whichever side is the "ink"
                        if (_reverse) System.Console.BackgroundColor = Palette[idx];
                        else System.Console.ForegroundColor = Palette[idx];
                    }
                    break;
            }
        }
        catch (IOException) { }
        catch (ArgumentOutOfRangeException) { }
    }

    void SetReverse(bool on)
    {
        if (!_colors || on == _reverse) return;
        _reverse = on;
        (System.Console.ForegroundColor, System.Console.BackgroundColor) =
            (System.Console.BackgroundColor, System.Console.ForegroundColor);
    }

    public string? ReadLine() => System.Console.In.ReadLine();

    public string GetKey()
    {
        if (System.Console.IsInputRedirected) return "";
        return System.Console.KeyAvailable ? System.Console.ReadKey(true).KeyChar.ToString() : "";
    }

    public void Poke(int address, int value)
    {
        if (!_colors) return;
        switch (address)
        {
            case 53281: // background
                System.Console.BackgroundColor = Palette[value & 15];
                System.Console.Clear();
                break;
            case 646: // text colour
                System.Console.ForegroundColor = Palette[value & 15];
                break;
        }
    }

    public void Restore()
    {
        if (_colors) System.Console.ResetColor();
    }
}
