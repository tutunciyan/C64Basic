using C64Basic.Core.Machine;

namespace C64Basic.Core.Runtime;

/// <summary>The KERNAL's file calls (OPEN, CLOSE, CHKIN, CHKOUT, CLRCHN, CLALL), on the same channels as BASIC's OPEN and PRINT#.</summary>
public sealed partial class Interpreter
{
    /// <summary>The channel CHRIN and GETIN read from after CHKIN; null means the keyboard.</summary>
    BasicFile? _kernalInput;

    /// <summary>Runs a KERNAL call that can fail: an error number goes to A with the carry set, like the real routines.</summary>
    static TrapResult Guarded(Cpu6502 c, Action action)
    {
        c.SetFlag(Cpu6502.FlagC, false);
        try { action(); }
        catch (BasicException e)
        {
            c.A = (byte)e.Code; // the error numbers of BASIC and of the KERNAL are the same
            c.SetFlag(Cpu6502.FlagC, true);
        }
        return TrapResult.Return;
    }

    TrapResult KernalOpen(Cpu6502 c) => Guarded(c, () =>
        OpenFile(_bus.Ram[0xB8], _bus.Ram[0xBA], _bus.Ram[0xB9], KernalFileName()));

    TrapResult KernalClose(Cpu6502 c) => Guarded(c, () => CloseFile(c.A));

    TrapResult KernalChkin(Cpu6502 c) => Guarded(c, () =>
    {
        if (!_files.TryGetValue(c.X, out var f)) throw new BasicException(ErrorCode.FileNotOpen);
        if (f.Writing && f.Kind != FileKind.Command) throw new BasicException(ErrorCode.NotInputFile);
        _kernalInput = f.Kind == FileKind.Keyboard ? null : f;
    });

    TrapResult KernalChkout(Cpu6502 c) => Guarded(c, () =>
    {
        if (!_files.TryGetValue(c.X, out var f)) throw new BasicException(ErrorCode.FileNotOpen);
        if (!f.Writing) throw new BasicException(ErrorCode.NotOutputFile);
        _cmdFile = f.Kind == FileKind.Screen ? null : f; // output now goes to the channel instead of the screen
    });

    TrapResult KernalClrchn(Cpu6502 c)
    {
        _cmdFile = null;
        _kernalInput = null;
        return TrapResult.Return;
    }

    TrapResult KernalClall(Cpu6502 c)
    {
        foreach (int n in _files.Keys.ToList()) CloseFile(n);
        return KernalClrchn(c);
    }

    /// <summary>The next character of the input channel, or -1 at the end (the status then holds 64).</summary>
    int ReadKernalInput() => ReadChar(_kernalInput!, refill: false);
}
