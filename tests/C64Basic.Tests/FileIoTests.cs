using C64Basic.Core.Runtime;

namespace C64Basic.Tests;

public class FileIoTests
{
    [Fact]
    public void WriteThenReadBack()
    {
        var (output, _, fs) = Basic.Session(
            "10 OPEN 1,8,1,\"DATA\"",
            "20 PRINT#1,\"HELLO\";",
            "25 PRINT#1",
            "30 PRINT#1,42",
            "40 CLOSE 1",
            "50 OPEN 2,8,0,\"DATA\"",
            "60 INPUT#2,A$,N",
            "70 PRINT A$;N;ST",
            "80 CLOSE 2",
            "RUN");
        Assert.Equal("HELLO 42  64 \n", output);
        Assert.Equal(new[] { "HELLO", " 42 " }, fs.Files["DATA"]);
    }

    [Fact]
    public void AppendMode()
    {
        var (_, interp, fs) = Basic.Session();
        fs.Files["LOG"] = new[] { "A" };
        interp.ProcessLine("OPEN 1,8,2,\"LOG,S,A\":PRINT#1,\"B\":CLOSE 1");
        Assert.Equal(new[] { "A", "B" }, fs.Files["LOG"]);
    }

    [Fact]
    public void GetHashReadsCharacters()
    {
        var (output, _, _) = Basic.Session(
            "OPEN 1,8,1,\"T\":PRINT#1,\"XY\";:CLOSE 1",
            "OPEN 1,8,0,\"T\"",
            "GET#1,A$:PRINT A$;:GET#1,A$:PRINT A$;:GET#1,B$:PRINT ST");
        Assert.Equal("XY 64 \n", output);
    }

    [Theory]
    [InlineData("OPEN 1,8,0,\"NOPE\"", "?FILE NOT FOUND  ERROR\n")]
    [InlineData("PRINT#5,1", "?FILE NOT OPEN  ERROR\n")]
    [InlineData("OPEN 1,8,1,\"X\":OPEN 1,8,1,\"Y\"", "?FILE OPEN  ERROR\n")]
    [InlineData("OPEN 1,8,1,\"X\":INPUT#1,A", "?NOT INPUT FILE  ERROR\n")]
    [InlineData("OPEN 1,2", "?DEVICE NOT PRESENT  ERROR\n")]
    [InlineData("OPEN 1,8,1", "?MISSING FILE NAME  ERROR\n")]
    public void Errors(string line, string expected) => Assert.Equal(expected, Basic.Run(line));

    [Fact]
    public void CmdRedirectsList()
    {
        var (output, _, fs) = Basic.Session("10 REM HI", "OPEN 4,8,1,\"L\":CMD 4:LIST:PRINT#4:CLOSE 4");
        Assert.Equal("READY.\n".Length > 0 ? "" : "", output);
        Assert.Equal(new[] { "10 REM HI" }, fs.Files["L"].Where(x => x.Length > 0).ToArray());
    }

    [Fact]
    public void CommandChannelStatus()
    {
        Assert.Equal(" 0 OK\n", Basic.Run("OPEN 15,8,15", "INPUT#15,E,E$:PRINT E;E$").Replace("  ", " "));
    }

    [Fact]
    public void SysClearsScreen() => Assert.Equal("\u0093", Basic.Run("SYS 58692"));

    [Fact]
    public void SysIntoEmptyRamHitsBrkAndEndsTheProgram() =>
        Assert.Equal("", Basic.Run("10 SYS 49152:PRINT \"NOT HERE\"", "RUN"));

    [Fact]
    public void WaitReturnsWhenConditionAlreadyTrue() =>
        Assert.Equal("DONE\n", Basic.Run("POKE 1000,4:WAIT 1000,4:PRINT \"DONE\""));
}

public class PetsciiTests
{
    [Fact]
    public void ChrAscRoundTrip() => Assert.Equal(" 109  110  205 \n", Basic.Run("PRINT ASC(CHR$(109));ASC(CHR$(110));ASC(CHR$(205))"));

    [Fact]
    public void GraphicsStayDistinctFromText() => Assert.Equal(" 0 \n", Basic.Run("PRINT CHR$(109)=\"m\""));

    [Fact]
    public void DiagonalGlyphs()
    {
        Assert.Equal('╲', Petscii.Glyph(Petscii.ToChar(109)));
        Assert.Equal('╱', Petscii.Glyph(Petscii.ToChar(110)));
        Assert.Equal('╲', Petscii.Glyph(Petscii.ToChar(205)));
    }
}

public class ScreenTests
{
    [Fact]
    public void ScreenCodeGlyphs()
    {
        Assert.Equal('●', Petscii.ScreenGlyph(81));
        Assert.Equal('A', Petscii.ScreenGlyph(1));
        Assert.Equal('╲', Petscii.ScreenGlyph(77));
        Assert.Equal('5', Petscii.ScreenGlyph(53));
    }

    [Fact]
    public void PokeColumnMovesCursor() => Assert.Equal(" 10 \n", Basic.Run("POKE 211,10:PRINT POS(0)"));

    [Fact]
    public void BallProgramRuns()
    {
        var output = Basic.Run(
            "10 printchr$(147):poke214,24:print:poke211,10:print\"line bouncing ball\"",
            "20 sm = 1024:x=int(rnd(1)*24):y=1:dy=1:dx=1",
            "30 if y>=22 or y < 1 then dy=dy*-1",
            "40 if x>=39 or x < 1 then dx=dx*-1",
            "50 x=x+(1*dx):y=y+(1*dy):poke sm+x+(y*40), 81 :fors=1to100:next: n=n+1: if n<200 then 30",
            "RUN");
        Assert.DoesNotContain("ERROR", output);
    }
}

public class CharRomTests
{
    [Fact]
    public void RomIsHiddenUntilIoIsSwitchedOut() =>
        Assert.Equal(" 0  30 \n", Basic.Run("PRINT PEEK(53248+10*8);:POKE 1,PEEK(1)AND251:PRINT PEEK(53248+10*8)"));

    [Fact]
    public void AscOfTypedLetterIsUpperCasePetscii() => Assert.Equal(" 74  74 \n", Basic.Run("PRINT ASC(\"j\");ASC(\"J\")"));

    [Fact]
    public void BannerPrintsBigLetters()
    {
        var output = Basic.Run(
            "10 dim cr(5):poke1,peek(1)and251:mg$=\"hi\":gosub 200:end",
            "200 fori=1tolen(mg$):cr(i)=asc(mid$(mg$,i,1))and191:nexti",
            "210 for r=0 to 7:for c=1 to len(mg$):a=peek(53248+cr(c)*8+r)",
            "240 for n=7 to 0 step-1:b(n)=int(a/2^n):a=a-(b(n)*2^n):next n",
            "250 fort=0 to 7:print mid$(\" \"+chr$(113),1+b(7-t),1);:next t",
            "260 next c:print:next r:return",
            "RUN");
        string ball = Petscii.ToChar(113).ToString();
        // first row of H (66 66 66 -> .XX..XX.) then I (3C -> ..XXXX..)
        Assert.StartsWith(" " + ball + ball + "  " + ball + ball + " " + "  " + ball + ball + ball + ball + "  \n", output);
    }
}
