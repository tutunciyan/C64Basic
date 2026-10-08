using C64Basic.Core.IO;
using C64Basic.Core.Runtime;

namespace C64Basic.Tests;

public class FidelityTests
{
    // ---------- the hidden screen behind --plain ----------
    [Fact]
    public void AShadowScreenLetsPeekSeeWhatWasPrinted()
    {
        var inner = new TestConsole();
        var interp = new Interpreter(new ShadowScreenConsole(inner), new MemoryFileSystem());
        foreach (var line in new[] { "PRINT CHR$(147);\"HELLO\"", "PRINT PEEK(1024);PEEK(1028);PEEK(214)" })
            interp.ProcessLine(line);
        Assert.Contains(" 8  15  1", inner.Output);          // H and O as screen codes, cursor on row 1
    }

    [Fact]
    public void TheShadowScreenStillPassesTextAndInputThrough()
    {
        var inner = new TestConsole(new[] { "ABC" });
        var shadow = new ShadowScreenConsole(inner);
        var interp = new Interpreter(shadow, new MemoryFileSystem());
        interp.ProcessLine("10 INPUT A$:PRINT \"<\";A$;\">\"");
        interp.ProcessLine("RUN");
        Assert.Contains("<ABC>", inner.Output);
    }

    // ---------- stock V2 behaviour in --strict ----------
    [Fact]
    public void StrictStringsStopAt255()
    {
        var output = Basic.RunStrict("A$=\"X\":FOR I=1 TO 7:A$=A$+A$:NEXT:PRINT LEN(A$):A$=A$+A$");
        Assert.Contains(" 128", output);
        Assert.Contains("?STRING TOO LONG", output);
    }

    [Fact]
    public void OnlyTheFirstTwoLettersOfANameCount()
    {
        Assert.Contains(" 6  6  6", Basic.RunStrict("AB1=5:ABC=6:PRINT AB1;ABC;AB"));
    }

    [Fact]
    public void StrictNumbersPrintLikeTheRealThing()
    {
        var output = Basic.RunStrict("PRINT 1E10;.1;100000000;.01;1E-5;1/3");
        Assert.Contains(" 1E+10  .1  100000000  .01  1E-05  .333333333", output);
    }

    [Fact]
    public void StrictForLoopsRunOnce()
    {
        Assert.Contains("ONCE", Basic.RunStrict("FOR I=1 TO 0:PRINT \"ONCE\":NEXT"));
    }

    [Fact]
    public void ModIsJustAVariableInStrictMode()
    {
        Assert.Contains("10  0  3", Basic.RunStrict("PRINT 10 MOD 3"));
    }

    // ---------- bad lines are kept ----------
    [Fact]
    public void ALineWithASyntaxErrorIsStoredAndListedUnchanged()
    {
        var output = Basic.Run("10 PRINT (", "20 PRINT \"OK\"", "LIST");
        Assert.Contains("10 PRINT (", output);
        Assert.Contains("20 PRINT \"OK\"", output);
    }

    [Fact]
    public void TheSyntaxErrorOnlyShowsWhenTheLineRuns()
    {
        var output = Basic.RunStrict("10 PRINT \"BEFORE\"", "20 PRINT (", "RUN");
        Assert.Contains("BEFORE", output);
        Assert.Contains("?SYNTAX  ERROR IN 20", output);
    }
}
