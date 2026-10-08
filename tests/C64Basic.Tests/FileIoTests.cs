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
    [InlineData("OPEN 1,4", "?DEVICE NOT PRESENT  ERROR\n")]
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
    public void SysUnknownIsIllegal() => Assert.Equal("?ILLEGAL QUANTITY  ERROR\n", Basic.Run("SYS 49152"));

    [Fact]
    public void WaitReturnsWhenConditionAlreadyTrue() =>
        Assert.Equal("DONE\n", Basic.Run("POKE 1000,4:WAIT 1000,4:PRINT \"DONE\""));
}
