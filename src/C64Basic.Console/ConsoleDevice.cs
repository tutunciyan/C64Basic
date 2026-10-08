using System.Text;
using C64Basic.Core.IO;
using C64Basic.Core.Machine;
using C64Basic.Core.Runtime;

namespace C64Basic.Console;

/// <summary>
/// Maps the interpreter's screen and keyboard onto the host terminal. With <c>emulateScreen</c> it shows a
/// full C64 display (<see cref="C64Screen"/>); otherwise output is a plain text stream.
/// </summary>
sealed class ConsoleDevice : IConsoleDevice
{
    readonly C64Screen? _screen;

    public ConsoleDevice(bool emulateScreen, int width = 0)
    {
        try { System.Console.OutputEncoding = Encoding.UTF8; } catch (IOException) { }
        if (emulateScreen && !System.Console.IsOutputRedirected && !System.Console.IsInputRedirected)
            _screen = new C64Screen(width);

        System.Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            BreakRequested = true;
        };
    }

    public bool HasScreen => _screen != null;

    public volatile bool _break;
    public bool BreakRequested { get => _break; set => _break = value; }

    public void Write(string text)
    {
        if (_screen != null) { _screen.Write(text); return; }

        var run = new StringBuilder();
        foreach (char c in text)
        {
            if (Petscii.IsGlyph(c)) run.Append(Petscii.Glyph(c));
            else if (c == '\r') run.Append('\n');
            else if (c == '\n' || (c >= ' ' && c < '\u0080') || c >= ' ') run.Append(c);
            else if (c == '\u0093' && !System.Console.IsOutputRedirected)
            {
                System.Console.Out.Write(run.ToString().Replace("\n", Environment.NewLine));
                run.Clear();
                System.Console.Clear();
            }
        }
        System.Console.Out.Write(run.ToString().Replace("\n", Environment.NewLine));
        System.Console.Out.Flush();
    }

    public string? ReadLine() => _screen != null ? _screen.ReadLine() : System.Console.In.ReadLine();

    public string GetKey()
    {
        if (System.Console.IsInputRedirected) return "";
        return System.Console.KeyAvailable ? System.Console.ReadKey(true).KeyChar.ToString() : "";
    }

    public void Attach(Bus bus) => _screen?.Attach(bus);

    public void Restore() => _screen?.Close();
}
