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

    readonly bool _colors;

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

    public void Write(string text)
    {
        int start = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] != '\u0093') continue;
            System.Console.Out.Write(text[start..i].Replace("\n", Environment.NewLine));
            if (!System.Console.IsOutputRedirected) System.Console.Clear();
            start = i + 1;
        }
        System.Console.Out.Write(text[start..].Replace("\n", Environment.NewLine));
        System.Console.Out.Flush();
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
