using C64Basic.Core.IO;
using C64Basic.Core.Runtime;

namespace C64Basic.Core;

/// <summary>The READY. prompt loop: reads lines from the device and feeds them to the interpreter.</summary>
public sealed class Repl
{
    readonly Interpreter _interp;
    readonly IConsoleDevice _dev;

    public Repl(Interpreter interpreter, IConsoleDevice device)
    {
        _interp = interpreter;
        _dev = device;
    }

    public void Banner()
    {
        _interp.Write(_interp.Banner);
    }

    /// <summary>Runs until the input ends.</summary>
    public void Run(bool showBanner = true)
    {
        if (showBanner) Banner();
        try
        {
            while (true)
            {
                if (_interp.AutoActive)
                {
                    _interp.Write($"{_interp.AutoNext} ");
                    string? typed = _dev.ReadLine();
                    if (typed == null) return;
                    _interp.ResetColumn();
                    _interp.ProcessAutoLine(typed);
                    if (!_interp.AutoActive) _interp.Write("READY.\n");
                    continue;
                }

                string? line = _dev.ReadLine();
                if (line == null) return;
                _interp.ResetColumn();
                if (_interp.ProcessLine(line)) PrintReady();
            }
        }
        catch (InputEndedException) { }
    }

    /// <summary>Loads a program from disk, runs it, and returns false if it ended with an error.</summary>
    public bool RunFile(string path)
    {
        _interp.LoadQuietly(path);
        if (_interp.LastRunFailed) return false;
        try { _interp.ProcessLine("RUN"); }
        catch (InputEndedException) { }
        return !_interp.LastRunFailed;
    }

    /// <summary>Prints READY. and keeps prompting; used after a program given on the command line has run.</summary>
    public void Resume()
    {
        PrintReady();
        Run(showBanner: false);
    }

    void PrintReady()
    {
        if (_interp.AutoActive) return;
        if (_interp.Col != 0) _interp.Write("\n");
        _interp.Write("READY.\n");
    }
}
