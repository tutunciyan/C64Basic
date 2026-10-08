using C64Basic.Core.Runtime;

namespace C64Basic.Tests;

/// <summary>SYS and USR from BASIC.</summary>
public class MachineCodeTests
{
    /// <summary>POKEs the bytes at an address, as a BASIC DATA loader would.</summary>
    static string Poke(int address, params int[] bytes) =>
        string.Join(":", bytes.Select((b, i) => $"POKE {address + i},{b}"));

    [Fact]
    public void SysRunsMachineCodeAndReturns() =>
        Assert.Equal("A", Basic.Run(Poke(49152, 169, 65, 32, 210, 255, 96), "SYS 49152"));   // LDA #65: JSR CHROUT: RTS

    [Fact]
    public void SysPassesAndReturnsRegisters()
    {
        // TXA, CLC, ADC #1, RTS: A = X + 1
        Assert.Equal(" 6 \n", Basic.Run(Poke(49152, 138, 24, 105, 1, 96), "POKE 781,5:SYS 49152:PRINT PEEK(780)"));
    }

    [Fact]
    public void SysKeepsTheBasicProgramRunning() =>
        Assert.Equal("1\n2\n", Basic.Run("10 PRINT 1:SYS 49152:PRINT 2", Poke(49152, 96), "RUN").Replace(" ", ""));

    [Fact]
    public void BrkEndsTheBasicProgram() =>
        Assert.Equal("", Basic.Run("10 SYS 49152:PRINT \"X\"", "RUN"));

    [Fact]
    public void UndocumentedOpcodeIsIllegalQuantity() =>
        Assert.Equal("?ILLEGAL QUANTITY  ERROR\n", Basic.Run(Poke(49152, 2), "SYS 49152"));

    [Fact]
    public void RomRoutineWithoutEmulationIsIllegalQuantity() =>
        Assert.Equal("?ILLEGAL QUANTITY  ERROR IN 10\n", Basic.Run(Poke(49152, 76, 0, 0xFF), "10 SYS 49152", "RUN"));

    [Fact]
    public void ExistingSysShortcutsStillWork()
    {
        Assert.Equal("\u0093", Basic.Run("SYS 58692"));
        Assert.Equal("Z", Basic.Run("POKE 780,90:SYS 65490"));
        Assert.Contains("COMMODORE 64 BASIC V2", Basic.Run("10 REM", "SYS 64738"));
    }

    [Fact]
    public void ResetViaSysClearsTheProgram()
    {
        var (_, interp, _) = Basic.Session("10 PRINT 1", "SYS 64738");
        Assert.Equal(0, interp.LineCount);
    }

    [Fact]
    public void MachineCodeCanPrintAString()
    {
        // LDX #0: loop: LDA $C010,X: BEQ done: JSR CHROUT: INX: BNE loop: done: RTS
        string program = Poke(49152, 162, 0, 189, 16, 192, 240, 6, 32, 210, 255, 232, 208, 245, 96)
            + ":" + Poke(49168, 72, 73, 0);
        Assert.Equal("HI", Basic.Run(program, "SYS 49152"));
    }

    [Fact]
    public void ChrinReadsALineFromTheKeyboard()
    {
        // JSR CHRIN: STA $C100 ... reads two characters (H, I) into memory
        string program = Poke(49152, 32, 207, 255, 141, 0, 193, 32, 207, 255, 141, 1, 193, 96);
        var console = new TestConsole(new[] { "hi" });
        var interp = new Interpreter(console, new MemoryFileSystem());
        interp.ProcessLine(program);
        interp.ProcessLine("SYS 49152:PRINT PEEK(49408);PEEK(49409)");
        Assert.Equal(" 72  73 \n", console.Output);
    }

    [Fact]
    public void GetinReturnsZeroWithoutAKeyAndKeyCodesOtherwise()
    {
        // JSR GETIN: STA $C100: RTS
        Assert.Equal(" 0 \n", Basic.Run(Poke(49152, 32, 228, 255, 141, 0, 193, 96), "POKE 49408,9:SYS 49152:PRINT PEEK(49408)"));
    }

    [Fact]
    public void PlotMovesTheCursor()
    {
        // LDX #2 : LDY #3 : CLC : JSR PLOT : RTS then print a mark
        string move = Poke(49152, 162, 2, 160, 3, 24, 32, 240, 255, 96);
        string output = Basic.Run(move, "SYS 49152:PRINT POS(0)");
        Assert.Contains(" 3 ", output);
    }

    [Fact]
    public void SetlfsAndSetnamFillZeroPage()
    {
        // LDA #2: LDX #8: LDY #0: JSR SETLFS: RTS
        var (_, interp, _) = Basic.Session(Poke(49152, 169, 2, 162, 8, 160, 0, 32, 186, 255, 96), "SYS 49152");
        Assert.Equal(2, interp.Bus.Ram[0xB8]);
        Assert.Equal(8, interp.Bus.Ram[0xBA]);
        Assert.Equal(0, interp.Bus.Ram[0xB9]);
    }

    [Fact]
    public void MachineCodeWritesTheScreenThroughTheBus()
    {
        var (_, interp, _) = Basic.Session(Poke(49152, 169, 1, 141, 0, 4, 96), "SYS 49152");
        Assert.Equal(1, interp.Bus.Ram[1024]);
    }

    [Fact]
    public void MachineCodeCanReadTheKeyboardMatrix()
    {
        // LDA $C5: STA $C100: RTS
        var console = new TestConsole();
        var interp = new Interpreter(console, new MemoryFileSystem());
        interp.Bus.Input = new TestInput().Press(9);
        interp.ProcessLine(Poke(49152, 165, 197, 141, 0, 193, 96));
        interp.ProcessLine("SYS 49152:PRINT PEEK(49408)");
        Assert.Equal(" 9 \n", console.Output);
    }

    // ---------- USR ----------
    static string Usr(string expression, params int[] code) =>
        Basic.Run(Poke(49152, code), "POKE 785,76:POKE 786,0:POKE 787,192:PRINT " + expression);

    [Fact]
    public void UsrWithoutAVectorIsIllegalQuantity() =>
        Assert.Equal("?ILLEGAL QUANTITY  ERROR\n", Basic.Run("PRINT USR(1)"));

    [Fact]
    public void UsrRoutineThatReturnsLeavesTheArgumentInPlace()
    {
        Assert.Equal(" 3.5 \n", Usr("USR(3.5)", 96));
        Assert.Equal("-12 \n", Usr("USR(-12)", 96).Replace(" -12", "-12"));
        Assert.Equal(" 0 \n", Usr("USR(0)", 96));
        Assert.Equal(" 1E+30 \n", Usr("USR(1E30)", 96));
        Assert.Equal(" 3.14159265 \n", Usr("USR(3.14159265)", 96));
    }

    [Fact]
    public void UsrRoutineCanChangeTheResult()
    {
        // INC $61 (exponent) doubles the value
        Assert.Equal(" 6 \n", Usr("USR(3)", 230, 97, 96));
        Assert.Equal(" 20 \n", Usr("USR(2.5)+USR(7.5)", 230, 97, 96));
    }

    [Fact]
    public void UsrFloatingPointFormatMatchesTheC64()
    {
        // 1 is exponent 129, mantissa 80 00 00 00; read the accumulator back with PEEK inside the routine's wake
        var (_, interp, _) = Basic.Session(Poke(49152, 96), "POKE 785,76:POKE 786,0:POKE 787,192", "X=USR(1)");
        Assert.Equal(129, interp.Bus.Ram[0x61]);
        Assert.Equal(0x80, interp.Bus.Ram[0x62]);
        Assert.Equal(0, interp.Bus.Ram[0x66]);
        (_, interp, _) = Basic.Session(Poke(49152, 96), "POKE 785,76:POKE 786,0:POKE 787,192", "X=USR(-10)");
        Assert.Equal(132, interp.Bus.Ram[0x61]);   // 10 = 0.625 * 2^4
        Assert.Equal(0xA0, interp.Bus.Ram[0x62]);
        Assert.Equal(0xFF, interp.Bus.Ram[0x66]);
    }

    [Fact]
    public void BreakInterruptsARunawayRoutine()
    {
        var console = new TestConsole();
        var interp = new Interpreter(console, new MemoryFileSystem());
        interp.ProcessLine(Poke(49152, 76, 0, 192));   // JMP $C000
        _ = Task.Run(async () => { await Task.Delay(50); console.BreakRequested = true; });
        interp.ProcessLine("SYS 49152");
        Assert.Equal("BREAK\n", console.Output);
    }
}
