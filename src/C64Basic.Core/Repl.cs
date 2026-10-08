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
        string title = _interp.Strict ? "COMMODORE 64 BASIC V2" : "COMMODORE 64 BASIC V2 (C# EDITION)";
        _interp.Write($"\n    **** {title} ****\n\n 64K RAM SYSTEM  38911 BASIC BYTES FREE\n\nREADY.\n");
    }

    /// <summary>Runs until the input ends.</summary>
    public void Run()
    {
        Banner();
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
        _interp.ProcessLine($"LOAD \"{path}\"");
        if (_interp.LastRunFailed) return false;
        try { _interp.ProcessLine("RUN"); }
        catch (InputEndedException) { }
        return !_interp.LastRunFailed;
    }

    void PrintReady()
    {
        if (_interp.AutoActive) return;
        if (_interp.Col != 0) _interp.Write("\n");
        _interp.Write("READY.\n");
    }
}
