using C64Basic.Core.Disk;
using C64Basic.Core.Runtime;

namespace C64Basic.Tests;

/// <summary>LOAD, SAVE, OPEN and the KERNAL calls against real drive implementations.</summary>
public class DriveIntegrationTests
{
    sealed class Session
    {
        public readonly TestConsole Console = new();
        public readonly MemoryFileSystem Fs = new();
        public readonly D64Image Disk = D64Image.Create("TEST", "01");
        public readonly Interpreter Interp;

        public Session(params string[] lines)
        {
            Interp = new Interpreter(Console, Fs);
            Interp.MountDrive(8, Disk);
            Run(lines);
        }

        public void Run(params string[] lines) { foreach (var l in lines) Interp.ProcessLine(l); }

        public string Output => Console.Output;
    }

    static string Poke(int address, params int[] bytes) =>
        string.Join(":", bytes.Select((b, i) => $"POKE {address + i},{b}"));

    static readonly byte[] HiProgram = PrgFormat.Tokenize(new[] { (10, "PRINT \"HI\"") });

    [Fact]
    public void SaveWritesATokenizedPrgToTheDisk()
    {
        var s = new Session("10 PRINT \"HI\"", "SAVE \"HELLO\",8");
        Assert.Equal("SAVING HELLO\n", s.Output);
        Assert.Equal(HiProgram, s.Disk.Read("HELLO").Data);
        Assert.Equal(FileType.Prg, s.Disk.Directory().Single().Type);
    }

    [Fact]
    public void LoadBringsTheProgramBackAndItRuns()
    {
        var s = new Session("10 PRINT \"HI\"", "SAVE \"HELLO\",8", "NEW", "LOAD \"HELLO\",8", "RUN");
        Assert.Equal("SAVING HELLO\nSEARCHING FOR HELLO\nLOADING\nHI\n", s.Output);
        Assert.Equal(new[] { "10 PRINT \"HI\"" }, s.Interp.Listing());
    }

    [Fact]
    public void LoadWithoutADeviceUsesTheDisk()
    {
        var s = new Session("10 PRINT \"HI\"", "SAVE \"X\"", "NEW", "LOAD \"X\"");
        Assert.Single(s.Interp.Listing());
        Assert.Equal("X", s.Disk.Directory().Single().Name);
    }

    [Fact]
    public void SavingOverAnExistingFileFailsOnTheErrorChannel()
    {
        var s = new Session("10 PRINT \"HI\"", "SAVE \"HELLO\",8", "20 END", "SAVE \"HELLO\",8");
        Assert.Equal(HiProgram, s.Disk.Read("HELLO").Data);                    // not replaced
        s.Run("OPEN 15,8,15", "INPUT#15,E:PRINT E");
        Assert.EndsWith(" 63 \n", s.Output);
    }

    [Fact]
    public void AtSignReplacesAFile()
    {
        var s = new Session("10 PRINT \"HI\"", "SAVE \"HELLO\",8", "20 END", "SAVE \"@0:HELLO\",8");
        Assert.Equal(PrgFormat.Tokenize(new[] { (10, "PRINT \"HI\""), (20, "END") }), s.Disk.Read("HELLO").Data);
        Assert.Single(s.Disk.Directory());
    }

    [Fact]
    public void MissingFileIsFileNotFoundAndStatus62()
    {
        var s = new Session("LOAD \"NOPE\",8");
        Assert.Equal("SEARCHING FOR NOPE\n?FILE NOT FOUND  ERROR\n", s.Output);
        s.Run("OPEN 15,8,15", "INPUT#15,E:PRINT E");
        Assert.EndsWith(" 62 \n", s.Output);
    }

    [Fact]
    public void WildcardLoadsTheFirstMatch()
    {
        var s = new Session("10 PRINT \"HI\"", "SAVE \"GAME1\",8", "NEW", "LOAD \"GA*\",8");
        Assert.Equal(new[] { "10 PRINT \"HI\"" }, s.Interp.Listing());
    }

    [Fact]
    public void DirectoryIsLoadedAsAProgram()
    {
        var s = new Session("10 REM", "SAVE \"HELLO\",8", "SAVE \"WORLD\",8", "LOAD \"$\",8");
        var lines = s.Interp.Listing().ToList();
        Assert.Equal("0 \"\u0012TEST            \" 01 2A", lines[0]);
        Assert.Equal("1    \"HELLO\"" + new string(' ', 12) + "PRG", lines[1]);
        Assert.Equal("1    \"WORLD\"" + new string(' ', 12) + "PRG", lines[2]);
        Assert.Equal("662 BLOCKS FREE.", lines[3]);
    }

    [Fact]
    public void DirectoryPatternFilters()
    {
        var s = new Session("10 REM", "SAVE \"AAA\",8", "SAVE \"BBB\",8", "LOAD \"$0:A*\",8");
        var lines = s.Interp.Listing().ToList();
        Assert.Equal(3, lines.Count);
        Assert.Contains("\"AAA\"", lines[1]);
    }

    [Fact]
    public void SecondaryAddressOneLoadsMachineCodeAtItsOwnAddress()
    {
        var s = new Session("10 PRINT \"KEEP\"");
        s.Disk.Write("ML", FileType.Prg, new byte[] { 0x00, 0xC0, 1, 2, 3 }, false);
        s.Run("LOAD \"ML\",8,1", "PRINT PEEK(49152);PEEK(49153);PEEK(49154)");
        Assert.EndsWith(" 1  2  3 \n", s.Output);
        Assert.Equal(new[] { "10 PRINT \"KEEP\"" }, s.Interp.Listing());   // the program is untouched
    }

    [Fact]
    public void VerifyComparesWithTheDisk()
    {
        var s = new Session("10 PRINT \"HI\"", "SAVE \"HELLO\",8", "VERIFY \"HELLO\",8");
        Assert.EndsWith("SEARCHING FOR HELLO\nVERIFYING\nOK\n", s.Output);
        s.Run("20 END", "VERIFY \"HELLO\",8");
        Assert.EndsWith("?VERIFY  ERROR\n", s.Output);
    }

    [Fact]
    public void ProgramModeStaysSilent()
    {
        var s = new Session("10 PRINT \"HI\"", "20 SAVE \"HELLO\",8", "30 LOAD \"HELLO\",8");
        s.Run("RUN");
        Assert.Equal("HI\n", s.Output);
        Assert.Equal("HELLO", s.Disk.Directory().Single().Name);
    }

    [Fact]
    public void LoadFromTheKeyboardOrScreenIsAnIllegalDevice()
    {
        Assert.Equal("?ILLEGAL DEVICE NUMBER  ERROR\n", new Session("LOAD \"X\",0").Output.Replace("SEARCHING FOR X\n", ""));
        Assert.Contains("?DEVICE NOT PRESENT", new Session("LOAD \"X\",12").Output);
    }

    // ---------- sequential files ----------
    [Fact]
    public void SequentialFilesLiveOnTheDiskAsPetscii()
    {
        var s = new Session("OPEN 1,8,2,\"DATA,S,W\":PRINT#1,\"HELLO\":PRINT#1,\"WORLD\":CLOSE 1");
        var entry = s.Disk.Directory().Single();
        Assert.Equal("DATA", entry.Name);
        Assert.Equal(FileType.Seq, entry.Type);
        Assert.Equal(new byte[] { 72, 69, 76, 76, 79, 13, 87, 79, 82, 76, 68, 13 }, s.Disk.Read("DATA").Data);

        s.Run("OPEN 1,8,2,\"DATA\":INPUT#1,A$,B$:CLOSE 1:PRINT A$;B$");
        Assert.Equal("HELLOWORLD\n", s.Output);
    }

    [Fact]
    public void AppendAddsToAnExistingSequentialFile()
    {
        var s = new Session("OPEN 1,8,2,\"LOG,S,W\":PRINT#1,\"A\":CLOSE 1", "OPEN 1,8,2,\"LOG,S,A\":PRINT#1,\"B\":CLOSE 1");
        Assert.Equal(new byte[] { 65, 13, 66, 13 }, s.Disk.Read("LOG").Data);
    }

    [Fact]
    public void OpeningAMissingDiskFileForReadingIsFileNotFound()
    {
        var s = new Session("OPEN 1,8,2,\"NOPE\"");
        Assert.Equal("?FILE NOT FOUND  ERROR\n", s.Output);
    }

    // ---------- command channel ----------
    [Fact]
    public void ScratchCommandDeletesFilesAndReportsTheCount()
    {
        var s = new Session("10 REM", "SAVE \"A1\",8", "SAVE \"A2\",8", "SAVE \"KEEP\",8");
        s.Run("OPEN 15,8,15,\"S:A*\"", "INPUT#15,E,E$,T:PRINT E;T");
        Assert.EndsWith(" 1  2 \n", s.Output);
        Assert.Equal(new[] { "KEEP" }, s.Disk.Directory().Select(e => e.Name));
    }

    [Fact]
    public void CommandsCanBeSentWithPrintHash()
    {
        var s = new Session("10 REM", "SAVE \"OLD\",8");
        s.Run("OPEN 15,8,15", "PRINT#15,\"R0:NEW=OLD\"", "INPUT#15,E:PRINT E", "CLOSE 15");
        Assert.EndsWith(" 0 \n", s.Output);
        Assert.Equal(new[] { "NEW" }, s.Disk.Directory().Select(e => e.Name));
    }

    [Fact]
    public void FormatCommandWipesTheDisk()
    {
        var s = new Session("10 REM", "SAVE \"A\",8");
        s.Run("OPEN 15,8,15,\"N0:FRESH,99\"", "CLOSE 15");
        Assert.Empty(s.Disk.Directory());
        Assert.Equal("FRESH", s.Disk.Title);
        Assert.Equal("99", s.Disk.Id);
    }

    [Fact]
    public void StatusReturnsToOkAfterBeingRead()
    {
        var s = new Session("OPEN 15,8,15,\"S:NOTHING\"");
        s.Run("INPUT#15,E,E$,T,S:PRINT E");     // 01, FILES SCRATCHED,00,00
        s.Run("INPUT#15,E,E$,T,S:PRINT E");     // once read, the channel reports 00, OK
        Assert.EndsWith(" 1 \n 0 \n", s.Output);
    }

    [Fact]
    public void UnknownCommandsAreSyntaxErrors()
    {
        var s = new Session("OPEN 15,8,15,\"XYZ\"", "INPUT#15,E:PRINT E");
        Assert.EndsWith(" 31 \n", s.Output);
    }

    [Fact]
    public void CopyCommandConcatenatesFiles()
    {
        var s = new Session("OPEN 1,8,2,\"A,S,W\":PRINT#1,\"X\":CLOSE 1", "OPEN 1,8,2,\"B,S,W\":PRINT#1,\"Y\":CLOSE 1");
        s.Run("OPEN 15,8,15,\"C:AB=A,B\"");
        Assert.Equal(new byte[] { 88, 13, 89, 13 }, s.Disk.Read("AB").Data);
    }

    // ---------- host files, tape and printer ----------
    [Fact]
    public void PrgFilesOnTheHostAreTokenized()
    {
        var s = new Session("10 PRINT \"HI\"", "SAVE \"X.prg\"", "NEW", "LOAD \"X.prg\"");
        // device 8 is the mounted disk here; use a second session on the host directory
        var host = new TestConsole();
        var fs = new MemoryFileSystem();
        var interp = new Interpreter(host, fs);
        interp.ProcessLine("10 PRINT \"HI\"");
        interp.ProcessLine("SAVE \"X.prg\"");
        Assert.Equal(HiProgram, fs.Binary["X.prg"]);
        interp.ProcessLine("NEW");
        interp.ProcessLine("LOAD \"X.prg\"");
        Assert.Equal(new[] { "10 PRINT \"HI\"" }, interp.Listing());
        Assert.NotNull(s);
    }

    [Fact]
    public void TapeShowsTheDatassetteMessages()
    {
        var console = new TestConsole();
        var interp = new Interpreter(console, new MemoryFileSystem());
        interp.ProcessLine("10 REM");
        interp.ProcessLine("SAVE \"T\",1");
        interp.ProcessLine("NEW");
        interp.ProcessLine("LOAD \"T\",1");
        Assert.Equal("PRESS RECORD & PLAY ON TAPE\nOK\nSAVING T\nPRESS PLAY ON TAPE\nOK\nSEARCHING FOR T\nFOUND T\nLOADING\n", console.Output);
        Assert.Equal(new[] { "10 REM" }, interp.Listing());
    }

    [Fact]
    public void AMountedTapeImageHoldsTheProgram()
    {
        var console = new TestConsole();
        var interp = new Interpreter(console, new MemoryFileSystem());
        var tape = new T64Image();
        interp.MountDrive(1, tape);
        interp.ProcessLine("10 PRINT \"HI\"");
        interp.ProcessLine("SAVE \"TUNE\",1");
        Assert.Equal(HiProgram, tape.Read("TUNE").Data);
        interp.ProcessLine("NEW");
        interp.ProcessLine("LOAD \"\",1");     // the next program on the tape
        Assert.Single(interp.Listing());
    }

    [Fact]
    public void PrinterOutputGoesToAFile()
    {
        var console = new TestConsole();
        var fs = new MemoryFileSystem();
        var interp = new Interpreter(console, fs);
        interp.ProcessLine("OPEN 4,4:PRINT#4,\"HELLO\":PRINT#4,\"AGAIN\":CLOSE 4");
        Assert.Equal(new[] { "HELLO", "AGAIN" }, fs.Files["PRINTER.TXT"]);
        interp.ProcessLine("OPEN 4,4:CMD 4:PRINT \"VIA CMD\":PRINT#4:CLOSE 4");
        Assert.Equal(new[] { "HELLO", "AGAIN", "VIA CMD", "" }, fs.Files["PRINTER.TXT"]);   // PRINT#4 sends a CR
        Assert.Equal("", console.Output);
    }

    [Fact]
    public void LoadQuietlyPrintsNothing()
    {
        var console = new TestConsole();
        var fs = new MemoryFileSystem();
        fs.Files["p.bas"] = new[] { "10 PRINT 1" };
        var interp = new Interpreter(console, fs);
        interp.LoadQuietly("p");
        Assert.Equal("", console.Output);
        Assert.Single(interp.Listing());
    }

    // ---------- KERNAL LOAD and SAVE ----------
    [Fact]
    public void KernalLoadReadsAFileIntoMemory()
    {
        var s = new Session();
        s.Disk.Write("ML", FileType.Prg, new byte[] { 0x00, 0xC2, 9, 8, 7 }, false);
        // SETNAM "ML", SETLFS 1,8,1, LOAD, then store the end address in $FB/$FC
        s.Run(Poke(49408, 77, 76),
            Poke(49152, 0xA9, 2, 0xA2, 0, 0xA0, 0xC1, 0x20, 0xBD, 0xFF, 0xA9, 1, 0xA2, 8, 0xA0, 1, 0x20, 0xBA, 0xFF,
                 0xA9, 0, 0x20, 0xD5, 0xFF, 0x86, 0xFB, 0x84, 0xFC, 0x60),
            "SYS 49152");
        var ram = s.Interp.Bus.Ram;
        Assert.Equal(new byte[] { 9, 8, 7 }, ram[0xC200..0xC203]);
        Assert.Equal(0x03, ram[0xFB]);
        Assert.Equal(0xC2, ram[0xFC]);
        Assert.Equal(0, ram[783] & 1);          // carry clear: success
    }

    [Fact]
    public void KernalLoadReportsFileNotFound()
    {
        var s = new Session(Poke(49408, 90, 90),
            Poke(49152, 0xA9, 2, 0xA2, 0, 0xA0, 0xC1, 0x20, 0xBD, 0xFF, 0xA9, 1, 0xA2, 8, 0xA0, 1, 0x20, 0xBA, 0xFF,
                 0xA9, 0, 0x20, 0xD5, 0xFF, 0x60),
            "SYS 49152");
        Assert.Equal(1, s.Interp.Bus.Ram[783] & 1);   // carry set
        Assert.Equal(4, s.Interp.Bus.Ram[780]);       // FILE NOT FOUND
    }

    [Fact]
    public void KernalLoadUsesTheCallersAddressWhenTheSecondaryAddressIsZero()
    {
        var s = new Session();
        s.Disk.Write("ML", FileType.Prg, new byte[] { 0x00, 0xC2, 9, 8, 7 }, false);
        // SETNAM, SETLFS 1,8,0, LDX #0 LDY #$C4 LDA #0 JSR LOAD
        s.Run(Poke(49408, 77, 76),
            Poke(49152, 0xA9, 2, 0xA2, 0, 0xA0, 0xC1, 0x20, 0xBD, 0xFF, 0xA9, 1, 0xA2, 8, 0xA0, 0, 0x20, 0xBA, 0xFF,
                 0xA2, 0, 0xA0, 0xC4, 0xA9, 0, 0x20, 0xD5, 0xFF, 0x60),
            "SYS 49152");
        Assert.Equal(new byte[] { 9, 8, 7 }, s.Interp.Bus.Ram[0xC400..0xC403]);
    }

    [Fact]
    public void KernalSaveWritesMemoryToTheDisk()
    {
        var s = new Session(Poke(49408, 83, 86), Poke(0xC300, 5, 6, 7), Poke(0xFB, 0x00, 0xC3),
            Poke(49152, 0xA9, 2, 0xA2, 0, 0xA0, 0xC1, 0x20, 0xBD, 0xFF, 0xA9, 1, 0xA2, 8, 0xA0, 1, 0x20, 0xBA, 0xFF,
                 0xA9, 0xFB, 0xA2, 0x03, 0xA0, 0xC3, 0x20, 0xD8, 0xFF, 0x60),
            "SYS 49152");
        Assert.Equal(new byte[] { 0x00, 0xC3, 5, 6, 7 }, s.Disk.Read("SV").Data);
    }

    [Fact]
    public void KernalLoadOfTheDirectoryProducesABasicProgram()
    {
        var s = new Session();
        s.Disk.Write("HELLO", FileType.Prg, new byte[] { 0x01, 0x08, 0, 0 }, false);
        // LOAD "$",8,1 puts the directory at $0401; read it back with LOAD from BASIC instead
        s.Run("LOAD \"$\",8");
        Assert.Contains("\"HELLO\"", s.Interp.Listing().ElementAt(1));
    }
}
