using System.Text;
using C64Basic.Core;
using C64Basic.Core.Disk;
using C64Basic.Core.IO;
using C64Basic.Core.Runtime;

namespace C64Basic.Tests;

public class StateTests
{
    /// <summary>A console that lets the test react to output, to save the state while a program runs.</summary>
    sealed class HookConsole : IConsoleDevice
    {
        readonly StringBuilder _out = new();
        public Action<string>? OnWrite;
        public string Output => _out.ToString();
        public bool BreakRequested { get; set; }
        public void Write(string text) { _out.Append(text); OnWrite?.Invoke(text); }
        public string? ReadLine() => null;
        public string GetKey() => "";
    }

    static (Interpreter Interp, TestConsole Console) Make(Func<double>? clock = null)
    {
        var console = new TestConsole();
        var interp = new Interpreter(console, new MemoryFileSystem());
        if (clock != null) interp.Bus.Seconds = clock;
        return (interp, console);
    }

    static string Run(Interpreter interp, TestConsole console, string line)
    {
        int before = console.Output.Length;
        interp.ProcessLine(line);
        return console.Output[before..];
    }

    // ---------- what is saved ----------
    [Fact]
    public void TheProgramAndVariablesComeBack()
    {
        var (a, ca) = Make();
        a.ProcessLine("10 PRINT \"HELLO\"");
        a.ProcessLine("20 END");
        a.ProcessLine("X=42:A$=\"TEXT\":DIM B(2,3):B(1,2)=7:C$(4)=\"ARR\"");
        var bytes = a.SaveState();

        var (b, cb) = Make();
        b.LoadState(bytes);
        Assert.Equal(new[] { "10 PRINT \"HELLO\"", "20 END" }, b.Listing());
        Assert.Equal(" 42 TEXT 7 ARR\n".Replace(" 42 ", " 42 "), Run(b, cb, "PRINT X;A$;B(1,2);C$(4)").Replace("  ", " "));
        Assert.Equal(" 0 \n", Run(b, cb, "PRINT B(0,0)"));
        Assert.Equal("HELLO\n", Run(b, cb, "RUN"));
        Assert.NotNull(ca);
    }

    [Fact]
    public void MemoryAndChipRegistersComeBack()
    {
        var (a, _) = Make();
        a.ProcessLine("POKE 49152,123:POKE 1024,1:POKE 55296,5:POKE 53280,2:POKE 53281,7:POKE 53269,1:POKE 54296,15");
        var bytes = a.SaveState();

        var (b, cb) = Make();
        b.LoadState(bytes);
        Assert.Equal(123, b.Bus.Ram[49152]);
        Assert.Equal(1, b.Bus.Ram[1024]);
        Assert.Equal(5, b.Bus.Color.Data[0]);
        Assert.Equal(2, b.Bus.Vic.Border);
        Assert.Equal(7, b.Bus.Vic.Background);
        Assert.Equal(1, b.Bus.Read(0xD015));
        Assert.Equal(" 123 \n", Run(b, cb, "PRINT PEEK(49152)"));
    }

    [Fact]
    public void SoundRegistersComeBackAndAHeldNoteKeepsPlaying()
    {
        var (a, _) = Make();
        a.ProcessLine("POKE 54296,15:POKE 54277,0:POKE 54278,240:POKE 54273,29:POKE 54272,69:POKE 54276,17");
        var bytes = a.SaveState();
        var (b, _) = Make();
        b.LoadState(bytes);
        var buffer = new short[44100];
        b.Bus.Sound.Render(buffer, 44100);
        int crossings = 0;
        for (int i = 1; i < buffer.Length; i++) if (buffer[i - 1] < 0 != buffer[i] < 0) crossings++;
        Assert.InRange(crossings, 870, 890);                  // the 440 Hz note, without an attack gap
    }

    [Fact]
    public void ForLoopsAndGosubsContinueAfterALoadMidRun()
    {
        var program = new[]
        {
            "10 FOR I=1 TO 4", "20 PRINT I", "30 GOSUB 100", "40 NEXT", "50 END", "100 PRINT \"SUB\";I", "110 RETURN",
        };

        // run once, saving the state right after the first SUB line
        var hook = new HookConsole();
        var first = new Interpreter(hook, new MemoryFileSystem());
        foreach (var line in program) first.ProcessLine(line);
        byte[]? saved = null;
        hook.OnWrite = text =>
        {
            if (saved == null && text.Contains("SUB")) first.SaveStateLater(b => saved = b);
        };
        first.ProcessLine("RUN");
        Assert.NotNull(saved);
        string fullRun = hook.Output;

        // load into a fresh machine and carry on
        var (second, console) = Make();
        var result = second.LoadState(saved!);
        Assert.True(result.WasRunning);
        Assert.True(result.NeedsContinue);
        string rest = Run(second, console, "CONT");

        Assert.EndsWith(rest, fullRun);                      // the tail of the original run
        Assert.StartsWith("2", rest.Replace(" ", "").Replace("\n", ""));
        Assert.Contains("SUB4", rest.Replace(" ", "").Replace("\n", ""));
        Assert.DoesNotContain("SUB1", rest.Replace(" ", ""));
    }

    [Fact]
    public void DefFnFunctionsAreRestored()
    {
        var (a, ca) = Make();
        a.ProcessLine("10 DEF FNA(X)=X*X+1");
        a.ProcessLine("20 DEF FNB(Y)=FNA(Y)*2");
        a.ProcessLine("RUN");
        Assert.Equal(" 10 \n", Run(a, ca, "PRINT FNA(3)"));
        var bytes = a.SaveState();

        var (b, cb) = Make();
        b.LoadState(bytes);
        Assert.Equal(" 10 \n", Run(b, cb, "PRINT FNA(3)"));
        Assert.Equal(" 20 \n", Run(b, cb, "PRINT FNB(3)"));
    }

    [Fact]
    public void TheDataPointerContinues()
    {
        var (a, ca) = Make();
        a.ProcessLine("10 DATA 1,2,3,4,5");
        a.ProcessLine("20 READ A,B");
        a.ProcessLine("RUN");
        var bytes = a.SaveState();

        var (b, cb) = Make();
        b.LoadState(bytes);
        Assert.Equal(" 3 \n", Run(b, cb, "READ C:PRINT C"));      // carries on after the two items already read
        Assert.NotNull(ca);
    }

    [Fact]
    public void TheClockKeepsCountingFromWhereItWas()
    {
        double timeA = 100;
        var (a, _) = Make(() => timeA);
        a.ProcessLine("TI$=\"120000\"");
        timeA += 30;                                          // half a minute later
        var bytes = a.SaveState();

        double timeB = 5000;                                  // a different machine with a different uptime
        var (b, cb) = Make(() => timeB);
        b.LoadState(bytes);
        Assert.Equal("120030\n", Run(b, cb, "PRINT TI$").Trim() + "\n");
        timeB += 10;
        Assert.Equal("120040\n", Run(b, cb, "PRINT TI$").Trim() + "\n");
    }

    [Fact]
    public void CiaTimersRunOnFromTheLoad()
    {
        double now = 0;
        var (a, _) = Make(() => now);
        a.Bus.Write(0xDD0E, 0);
        a.Bus.Write(0xDD04, 0xE8); a.Bus.Write(0xDD05, 0x03);        // latch 1000
        a.Bus.Write(0xDD0E, 1);
        now = 100 / Cia1Hz;                                           // 100 cycles in
        var bytes = a.SaveState();

        double later = 7;
        var (b, _) = Make(() => later);
        b.LoadState(bytes);
        later += 200 / Cia1Hz;
        int counter = b.Bus.Read(0xDD04) | b.Bus.Read(0xDD05) << 8;
        Assert.InRange(counter, 1000 - 100 - 200 - 2, 1000 - 100 - 200 + 2);
    }

    const double Cia1Hz = C64Basic.Core.Machine.Cia.ClockHz;

    [Fact]
    public void OpenFilesAreClosed()
    {
        var (a, ca) = Make();
        a.ProcessLine("OPEN 1,8,1,\"X,S,W\"");
        var bytes = a.SaveState();
        var (b, cb) = Make();
        b.ProcessLine("OPEN 2,8,1,\"Y,S,W\"");
        b.LoadState(bytes);
        Assert.Contains("FILE NOT OPEN", Run(b, cb, "PRINT#1,\"A\""));
        Assert.Contains("FILE NOT OPEN", Run(b, cb, "PRINT#2,\"A\""));
        Assert.NotNull(ca);
    }

    [Fact]
    public void ADirectoryListingKeepsItsOrder()
    {
        var console = new TestConsole();
        var a = new Interpreter(console, new MemoryFileSystem());
        a.MountDrive(8, D64Image.Create("T", "01"));
        a.ProcessLine("10 REM");
        a.ProcessLine("SAVE \"A\",8");
        a.ProcessLine("SAVE \"B\",8");
        a.ProcessLine("LOAD \"$\",8");
        var before = a.Listing().ToList();

        var b = new Interpreter(new TestConsole(), new MemoryFileSystem());
        b.LoadState(a.SaveState());
        Assert.Equal(before, b.Listing());
        b.ProcessLine("1 REM NEW");
        Assert.Equal("1 REM NEW", b.Listing().ElementAt(1));
        Assert.Equal(before.Count, b.Listing().Count());
    }

    [Fact]
    public void LoadingAnIdleStateStopsARunningProgram()
    {
        var (idle, _) = Make();
        idle.ProcessLine("10 PRINT \"IDLE\"");
        var idleState = idle.SaveState();

        var hook = new HookConsole();
        var running = new Interpreter(hook, new MemoryFileSystem());
        running.ProcessLine("10 PRINT \"LOOP\":GOTO 10");
        int writes = 0;
        StateLoadResult? result = null;
        hook.OnWrite = _ =>
        {
            if (++writes == 5) running.LoadStateLater(idleState, (r, e) => result = r);
            if (writes > 200) hook.BreakRequested = true;      // safety net so a bug cannot hang the test
        };
        running.ProcessLine("RUN");

        Assert.NotNull(result);
        Assert.False(result!.Value.WasRunning);
        Assert.True(writes < 50);                              // it stopped soon after the load
        Assert.Equal(new[] { "10 PRINT \"IDLE\"" }, running.Listing());
    }

    // ---------- damaged files ----------
    [Fact]
    public void WrongFilesAreRefusedAndNothingChanges()
    {
        var (a, ca) = Make();
        a.ProcessLine("10 REM KEEP");
        a.ProcessLine("X=5");
        Assert.Throws<InvalidDataException>(() => a.LoadState(Encoding.ASCII.GetBytes("this is not a state file")));
        Assert.Throws<InvalidDataException>(() => a.LoadState(Array.Empty<byte>()));
        Assert.Equal(new[] { "10 REM KEEP" }, a.Listing());
        Assert.Equal(" 5 \n", Run(a, ca, "PRINT X"));
    }

    [Fact]
    public void TruncatedStatesAreRefusedAndNothingChanges()
    {
        var (a, ca) = Make();
        a.ProcessLine("10 REM ORIGINAL");
        var good = a.SaveState();

        var (b, cb) = Make();
        b.ProcessLine("10 REM TARGET");
        b.ProcessLine("Y=9");
        foreach (int keep in new[] { 8, 40, good.Length / 2, good.Length - 1 })
            Assert.ThrowsAny<Exception>(() => b.LoadState(good[..keep]));
        Assert.Equal(new[] { "10 REM TARGET" }, b.Listing());
        Assert.Equal(" 9 \n", Run(b, cb, "PRINT Y"));
        Assert.NotNull(ca);
    }

    [Fact]
    public void ANewerVersionIsRefused()
    {
        var (a, _) = Make();
        var bytes = a.SaveState();
        bytes[6] = 99;                                         // the version number follows the 6-byte magic
        Assert.Contains("version", Assert.Throws<InvalidDataException>(() => a.LoadState(bytes)).Message);
    }

    [Fact]
    public void AbsurdCountsAreRefused()
    {
        var (a, _) = Make();
        var bytes = a.SaveState();
        // magic(6) version(4) running(1) curLine(4) curStmt(4) unordered(1) then the line count
        BitConverter.GetBytes(int.MaxValue).CopyTo(bytes, 20);
        Assert.Throws<InvalidDataException>(() => a.LoadState(bytes));
    }

    [Fact]
    public void SavingTwiceGivesTheSameBytesForTheSameMachine()
    {
        double now = 1;
        var (a, _) = Make(() => now);
        a.ProcessLine("10 PRINT 1");
        a.ProcessLine("X=1");
        Assert.Equal(a.SaveState(), a.SaveState());
    }

    // ---------- from another thread ----------
    [Fact]
    public void AStateCanBeSavedWhileTheMachineWaitsAtThePrompt()
    {
        var console = new ScreenConsole();
        var interp = new Interpreter(console, new MemoryFileSystem());
        var thread = new Thread(() => new Repl(interp, console).Run()) { IsBackground = true };
        thread.Start();
        console.Inject("10 PRINT \"SAVED\"\rX=77\r");
        Assert.True(SpinWait.SpinUntil(() => console.ScreenText().Contains("X=77") && interp.LineCount == 1, 10000));
        Thread.Sleep(100);

        byte[]? saved = null;
        interp.SaveStateLater(b => saved = b);
        Assert.True(SpinWait.SpinUntil(() => saved != null, 10000));

        var other = new Interpreter(new TestConsole(), new MemoryFileSystem());
        other.LoadState(saved!);
        Assert.Equal(new[] { "10 PRINT \"SAVED\"" }, other.Listing());
        console.Close();
        Assert.True(thread.Join(10000));
    }

    [Fact]
    public void AStateCanBeLoadedWhileTheMachineWaitsAtThePrompt()
    {
        var (source, _) = Make();
        source.ProcessLine("10 PRINT \"RESTORED\"");
        var state = source.SaveState();

        var console = new ScreenConsole();
        var interp = new Interpreter(console, new MemoryFileSystem());
        var thread = new Thread(() => new Repl(interp, console).Run()) { IsBackground = true };
        thread.Start();
        Assert.True(SpinWait.SpinUntil(() => console.CursorVisible, 10000));

        StateLoadResult? result = null;
        Exception? error = null;
        bool done = false;
        interp.LoadStateLater(state, (r, e) => { result = r; error = e; done = true; });
        Assert.True(SpinWait.SpinUntil(() => done, 10000));
        Assert.Null(error);
        Assert.False(result!.Value.WasRunning);
        Assert.Equal(new[] { "10 PRINT \"RESTORED\"" }, interp.Listing());
        console.Close();
        Assert.True(thread.Join(10000));
    }

    [Fact]
    public void ABadStateReportsTheErrorToTheCaller()
    {
        var (interp, _) = Make();
        Exception? error = null;
        interp.LoadStateLater(new byte[] { 1, 2, 3 }, (r, e) => error = e);
        interp.RunPendingRequests();
        Assert.IsType<InvalidDataException>(error);
    }
}
