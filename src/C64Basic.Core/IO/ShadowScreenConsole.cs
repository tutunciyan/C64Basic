using C64Basic.Core.Machine;

namespace C64Basic.Core.IO;

/// <summary>
/// Wraps a plain console (a pipe, a log file) and keeps a hidden C64 screen up to date behind it, so PEEK of screen RAM, colour
/// RAM and the cursor variables (214/211) see what was printed even though nothing is drawn.
/// </summary>
public sealed class ShadowScreenConsole : IConsoleDevice
{
    readonly IConsoleDevice _inner;
    ScreenEditor? _editor;

    public ShadowScreenConsole(IConsoleDevice inner) => _inner = inner;

    public bool BreakRequested
    {
        get => _inner.BreakRequested;
        set => _inner.BreakRequested = value;
    }

    public Action? WhileWaiting
    {
        get => _inner.WhileWaiting;
        set => _inner.WhileWaiting = value;
    }

    public void Attach(Bus bus)
    {
        _inner.Attach(bus);
        _editor = new ScreenEditor(bus);
    }

    public void Write(string text)
    {
        _editor?.Write(text);
        _inner.Write(text);
    }

    public string? ReadLine() => _inner.ReadLine();

    public string GetKey() => _inner.GetKey();
}
