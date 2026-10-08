using C64Basic.Core.Disk;
using C64Basic.Core.Runtime;

namespace C64Basic.Tests;

public class DosErrorTests
{
    sealed class Session
    {
        public readonly TestConsole Console = new();
        public readonly D64Image Disk = D64Image.Create("TEST", "01");
        public readonly Interpreter Interp;

        public Session()
        {
            Interp = new Interpreter(Console, new MemoryFileSystem());
            Interp.MountDrive(8, Disk);
        }

        public Session Run(params string[] lines) { foreach (var l in lines) Interp.ProcessLine(l); return this; }

        /// <summary>The text the program printed after a marker.</summary>
        public string After(string marker) { var o = Console.Output; return o[(o.LastIndexOf(marker) + marker.Length)..]; }
    }

    static string Status(params string[] setup)
    {
        var s = new Session().Run(setup).Run("OPEN 15,8,15", "INPUT#15,A$,B$,C$,D$", "PRINT \"[\";A$;\"|\";B$;\"]\"");
        return s.After("[").Split(']')[0];
    }

    // ---------- 60, 63, 64 ----------
    [Fact]
    public void ReadingAFileThatIsBeingWrittenIsError60()
    {
        Assert.Equal("60|WRITE FILE OPEN", Status("OPEN 2,8,2,\"DATA,S,W\"", "PRINT#2,\"HI\"", "OPEN 3,8,3,\"DATA,S,R\""));
    }

    [Fact]
    public void WritingAnExistingFileIsError63()
    {
        Assert.Equal("63|FILE EXISTS", Status("OPEN 2,8,2,\"DATA,S,W\"", "PRINT#2,\"ONE\"", "CLOSE 2", "OPEN 2,8,2,\"DATA,S,W\"", "PRINT#2,\"TWO\"", "CLOSE 2"));
    }

    [Fact]
    public void TheFailedWriteLeavesTheOriginalAlone()
    {
        var s = new Session().Run("OPEN 2,8,2,\"DATA,S,W\"", "PRINT#2,\"ONE\"", "CLOSE 2",
                                  "OPEN 2,8,2,\"DATA,S,W\"", "PRINT#2,\"TWO\"", "CLOSE 2",
                                  "OPEN 2,8,2,\"DATA,S,R\"", "INPUT#2,A$", "PRINT \"<\";A$;\">\"");
        Assert.Contains("<ONE>", s.Console.Output);
    }

    [Fact]
    public void AtSignReplacesTheFile()
    {
        var s = new Session().Run("OPEN 2,8,2,\"DATA,S,W\"", "PRINT#2,\"ONE\"", "CLOSE 2",
                                  "OPEN 2,8,2,\"@0:DATA,S,W\"", "PRINT#2,\"TWO\"", "CLOSE 2",
                                  "OPEN 2,8,2,\"DATA,S,R\"", "INPUT#2,A$", "PRINT \"<\";A$;\">\"");
        Assert.Contains("<TWO>", s.Console.Output);
    }

    [Fact]
    public void OpeningAProgramAsSequentialIsError64()
    {
        Assert.Equal("64|FILE TYPE MISMATCH", Status("10 PRINT 1", "SAVE\"PROG\",8", "OPEN 2,8,2,\"PROG,S,R\""));
    }

    [Fact]
    public void OpeningWithTheRightTypeWorks()
    {
        Assert.Equal("00|OK", Status("OPEN 2,8,2,\"DATA,S,W\"", "CLOSE 2", "OPEN 2,8,2,\"DATA,S,R\""));
    }

    // ---------- drive memory ----------
    [Fact]
    public void MemoryWriteThenReadReturnsTheBytes()
    {
        var s = new Session().Run("OPEN 15,8,15",
            "PRINT#15,\"M-W\"+CHR$(0)+CHR$(3)+CHR$(3)+CHR$(10)+CHR$(13)+CHR$(200)",
            "PRINT#15,\"M-R\"+CHR$(0)+CHR$(3)+CHR$(3)",
            "GET#15,A$", "GET#15,B$", "GET#15,C$", "PRINT \"<\";ASC(A$);ASC(B$);ASC(C$);\">\"");
        Assert.Contains("< 10  13  200 >", s.Console.Output);
    }

    [Fact]
    public void MemoryReadDefaultsToOneByteAndThenTheStatusReturns()
    {
        var s = new Session().Run("OPEN 15,8,15",
            "PRINT#15,\"M-W\"+CHR$(16)+CHR$(2)+CHR$(1)+CHR$(77)",
            "PRINT#15,\"M-R\"+CHR$(16)+CHR$(2)",
            "GET#15,A$", "INPUT#15,B$,C$", "PRINT \"<\";ASC(A$);\"|\";B$;\">\"");
        Assert.Contains("< 77 |00>", s.Console.Output);
    }

    [Fact]
    public void DriveRamIsMirroredEvery2K()
    {
        var m = new DriveMemory();
        m.Write(0x0010, 5);
        Assert.Equal(5, m.Read(0x0810));
        Assert.Equal(5, m.Read(0x1810));
        Assert.Equal(0, m.Read(0xC000));
        m.Write(0xC000, 9);
        Assert.Equal(0, m.Read(0xC000));                      // ROM
    }

    [Fact]
    public void MemoryExecuteIsASyntaxError()
    {
        Assert.Equal("31|SYNTAX ERROR", Status("OPEN 15,8,15", "PRINT#15,\"M-E\"+CHR$(0)+CHR$(5)"));
    }

    [Fact]
    public void AMemoryWriteTooLongForTheBufferIsRefused()
    {
        var r = DosCommands.Execute(D64Image.Create("T", "00"), "M-W" + (char)0 + (char)3 + (char)40, null, new DriveMemory());
        Assert.Equal(32, r.Code);
    }
}
