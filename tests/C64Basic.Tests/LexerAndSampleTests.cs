using C64Basic.Core.Lexing;
using C64Basic.Core.Machine;
using C64Basic.Core.Runtime;

namespace C64Basic.Tests;

public class LexerBehaviourTests
{
    static List<Token> Lex(string s, bool strict = false) => new Lexer(strict).Lex(s).Where(t => t.Kind != TokKind.Eol).ToList();

    static string Kinds(string s, bool strict = false) =>
        string.Join(" ", Lex(s, strict).Select(t => t.Kind == TokKind.Keyword ? $"[{t.Text}]" : t.Text));

    [Fact]
    public void KeywordsAreFoundWithoutSpaces() =>
        Assert.Equal("[FOR] I = 1 [TO] 10", Kinds("FORI=1TO10"));

    [Fact]
    public void KeywordsInsideNamesSplitTheNameLikeAC64()
    {
        Assert.Equal("[TO] TAL = 1", Kinds("TOTAL=1"));
        Assert.Equal("S [TO] RE = 1", Kinds("STORE=1"));
    }

    [Fact]
    public void QuestionMarkIsPrint() => Assert.Equal("[PRINT] 1", Kinds("?1"));

    [Fact]
    public void StringsAndNumbersKeepTheirText()
    {
        var toks = Lex("A$=\"HELLO TO YOU\":B=1.5E3");
        Assert.Contains(toks, t => t.Kind == TokKind.String && t.Text == "HELLO TO YOU");
        Assert.Contains(toks, t => t.Kind == TokKind.Number && t.Num == 1500);
    }

    [Fact]
    public void RemSwallowsTheRestOfTheLine()
    {
        var toks = Lex("REM GOTO 10 PRINT");
        Assert.Equal(2, toks.Count);
        Assert.Equal(TokKind.Raw, toks[1].Kind);
    }

    [Fact]
    public void DataStopsAtAColonOutsideQuotes()
    {
        var toks = Lex("DATA 1,\"A:B\",2:PRINT");
        Assert.Equal(TokKind.Raw, toks[1].Kind);
        Assert.Equal(" 1,\"A:B\",2", toks[1].Text);
        Assert.Equal(TokKind.Colon, toks[2].Kind);
    }

    [Fact]
    public void ComparisonOperatorsAreNormalised()
    {
        Assert.Equal("A <= B", Kinds("A=<B"));
        Assert.Equal("A >= B", Kinds("A=>B"));
        Assert.Equal("A <> B", Kinds("A><B"));
    }

    [Fact]
    public void NamesAreUpperCasedAndKeepTheirTypeSuffix()
    {
        var name = Lex("abc$=1")[0];
        Assert.Equal(TokKind.Name, name.Kind);
        Assert.Equal("ABC$", name.Text);
    }

    [Fact]
    public void NormalizeUpperCasesKeywordsOnly() =>
        Assert.Equal("PRINT \"hi\":GOTO 10", new Lexer(false).Normalize("print \"hi\":goto 10"));

    // ---------- extension keywords no longer steal variable names ----------
    [Theory]
    [InlineData("FIND=3:PRINT FIND", " 3 \n")]
    [InlineData("TRACE=7:PRINT TRACE", " 7 \n")]
    [InlineData("RENUMBER=9:PRINT RENUMBER", " 9 \n")]
    [InlineData("ELSE=1:PRINT ELSE", " 1 \n")]
    [InlineData("A=5:B=A+ELSE:PRINT B", " 5 \n")]
    public void ExtensionWordsWorkAsVariableNames(string line, string expected) =>
        Assert.Equal(expected, Basic.Run(line));

    [Fact]
    public void ExtensionCommandsStillWorkAtTheStartOfAStatement()
    {
        Assert.Equal("10 PRINT \"X\"\n", Basic.Run("10 PRINT \"X\"", "FIND \"X\""));
        Assert.Equal("", Basic.Run("10 REM", "DELETE 10", "LIST"));
        Assert.Equal("[10]HI\n", Basic.Run("10 PRINT \"HI\"", "TRACE ON", "RUN"));
    }

    [Fact]
    public void ElseStillSplitsIfStatements()
    {
        Assert.Equal(" 2 \n", Basic.Run("IF 0 THEN PRINT 1 ELSE PRINT 2"));
        Assert.Equal(" 1 \n", Basic.Run("IF 1 THEN PRINT 1 ELSE PRINT 2"));
        Assert.Equal("B\n", Basic.Run("A=0:IF A THEN PRINT \"A\" ELSE PRINT \"B\""));
        Assert.Equal(" 20 \n", Basic.Run("10 IF 0 THEN 10 ELSE 20", "20 PRINT 20", "RUN").Replace("\n\n", "\n"));
    }

    [Fact]
    public void StrictModeTreatsExtensionWordsAsPlainNames() =>
        Assert.Equal(" 3 \n", Basic.RunStrict("FIND=3:PRINT FIND"));

    [Fact]
    public void LinesCanBeSavedAndRenumberedWithAVariableNamedLikeACommand()
    {
        var (_, interp, _) = Basic.Session("10 FIND=1", "20 PRINT FIND", "RENUMBER 100,10");
        Assert.Equal(new[] { "100 FIND=1", "110 PRINT FIND" }, interp.Listing());
    }
}

public class ColorRamTests
{
    [Fact]
    public void MachineCodeCanFillAllOfColourRam()
    {
        // the whole 1 KB window exists, including the 24 bytes past the 1000 visible cells
        var bus = new Bus();
        bus.Write(0xDBFF, 7);
        Assert.Equal(7, bus.Read(0xDBFF));
        Assert.Equal(0xDBFF - 0xD800, 1023);
    }
}

/// <summary>The programs in <c>samples/</c> run to completion without an error.</summary>
public class SampleTests
{
    static string[] Sample(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "samples"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return File.ReadAllLines(Path.Combine(dir!.FullName, "samples", name));
    }

    static (string Output, Interpreter Interp) RunSample(string name)
    {
        var console = new TestConsole();
        var interp = new Interpreter(console, new MemoryFileSystem());
        foreach (var line in Sample(name)) interp.ProcessLine(line);
        interp.ProcessLine("RUN");
        return (console.Output, interp);
    }

    [Theory]
    [InlineData("hello.bas")]
    [InlineData("sieve.bas")]
    [InlineData("extras.bas")]
    [InlineData("sprite.bas")]
    [InlineData("sid.bas")]
    [InlineData("joystick.bas")]
    [InlineData("sysdemo.bas")]
    [InlineData("disk.bas")]
    public void RunsWithoutErrors(string name)
    {
        var (output, _) = RunSample(name);
        Assert.DoesNotContain("ERROR", output);
    }

    [Fact]
    public void SpriteSampleBuildsACircleAndLeavesTheSpriteOff()
    {
        var (_, interp) = RunSample("sprite.bas");
        var ram = interp.Bus.Ram;
        Assert.Equal(0xFF, ram[832 + 10 * 3 + 1]);   // the middle of the ball is solid
        Assert.Equal(0, ram[832]);                   // and the corner is empty
        Assert.Equal(0, interp.Bus.Read(0xD015));    // switched off at the end
    }

    [Fact]
    public void SidSampleEndsSilent()
    {
        var (_, interp) = RunSample("sid.bas");
        var buffer = new short[1000];
        interp.Bus.Sound.Render(buffer, 44100);
        Assert.All(buffer, s => Assert.Equal(0, s));
    }

    [Fact]
    public void MachineCodeSampleFillsScreenAndColourRam()
    {
        var (_, interp) = RunSample("sysdemo.bas");
        var ram = interp.Bus.Ram;
        Assert.Equal(7, ram[1024 + 7]);
        Assert.Equal(255, ram[1024 + 3 * 256 + 255]);
        Assert.Equal(9, interp.Bus.Color.Data[9] & 15);
        Assert.Equal(255 & 15, interp.Bus.Color.Data[3 * 256 + 255] & 15);
    }

    [Fact]
    public void DiskSampleRoundTripsAndCleansUp()
    {
        var (output, _) = RunSample("disk.bas");
        Assert.Contains(" 1  4  9  16  25 ", output);
        Assert.Contains(" 1 FILES SCRATCHED 1", output);
    }

    [Fact]
    public void JoystickSampleReadsThePort()
    {
        var console = new TestConsole();
        var interp = new Interpreter(console, new MemoryFileSystem());
        var input = new TestInput().Joy(2, 0b01000);          // right
        interp.Bus.Input = input;
        foreach (var line in Sample("joystick.bas")) interp.ProcessLine(line);
        interp.ProcessLine("RUN");
        // 3000 steps of +2 pixels, wrapped by the 9th X bit register logic: the sprite ended far right
        Assert.Equal(1, interp.Bus.Read(0xD010) & 1);
        input.Joy(2, 0b10000);                                  // fire stops it at once
    }
}
