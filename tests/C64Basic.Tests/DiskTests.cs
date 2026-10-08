using C64Basic.Core.Disk;

namespace C64Basic.Tests;

public class PrgFormatTests
{
    [Fact]
    public void TokenizesALine()
    {
        var prg = PrgFormat.Tokenize(new[] { (10, "PRINT \"HI\"") });
        // load address, link to the end marker, line 10, PRINT token, space, "HI", end of line, end of program
        Assert.Equal(new byte[] { 0x01, 0x08, 0x0C, 0x08, 0x0A, 0x00, 0x99, 0x20, 0x22, 0x48, 0x49, 0x22, 0x00, 0x00, 0x00 }, prg);
    }

    [Fact]
    public void LinkPointersChainThroughTheProgram()
    {
        var prg = PrgFormat.Tokenize(new[] { (10, "END"), (20, "END") });
        Assert.Equal(0x0801 + 4 + 1 + 1, prg[2] | prg[3] << 8);
        int second = (prg[2] | prg[3] << 8) - 0x0801 + 2;
        Assert.Equal(20, prg[second + 2]);
        Assert.Equal(0, prg[^1]);
        Assert.Equal(0, prg[^2]);
    }

    [Fact]
    public void RoundTripsKeywordsOperatorsAndStrings()
    {
        var lines = new (int, string)[]
        {
            (10, "FOR I=1 TO 10:PRINT I*2;\"TO PRINT\":NEXT"),
            (20, "IF A>=B THEN GOTO 100"),
            (30, "REM THIS IS A REMARK WITH PRINT AND GOTO"),
            (40, "DATA 1,2,\"A:B\",3:PRINT \"X\""),
            (50, "A$=LEFT$(B$,3)+MID$(C$,2,1):ON X GOSUB 1,2,3"),
            (60, "PRINT#1,A:INPUT#1,B:GET A$:OPEN 1,8,2,\"X\""),
            (70, "X=SQR(ABS(-3))^2/4-1"),
        };
        var again = PrgFormat.Detokenize(PrgFormat.Tokenize(lines));
        Assert.Equal(lines, again);
    }

    [Fact]
    public void QuestionMarkBecomesPrint()
    {
        var prg = PrgFormat.Tokenize(new[] { (1, "?\"A\"") });
        Assert.Equal(0x99, prg[6]);
        Assert.Equal("PRINT\"A\"", PrgFormat.Detokenize(prg)[0].Text);
    }

    [Fact]
    public void KeywordsAreFoundInsideNamesLikeTheRealCrunch()
    {
        // TOTAL is TO + TAL on a C64
        var prg = PrgFormat.Tokenize(new[] { (1, "TOTAL=1") });
        Assert.Equal(0xA4, prg[6]);
    }

    [Fact]
    public void RemAndDataTextIsNeverTokenized()
    {
        var prg = PrgFormat.Tokenize(new[] { (1, "REM GOTO"), (2, "DATA PRINT:END") });
        var back = PrgFormat.Detokenize(prg);
        Assert.Equal("REM GOTO", back[0].Text);
        Assert.Equal("DATA PRINT:END", back[1].Text);
        Assert.DoesNotContain(prg.Skip(6).Take(10), b => b == 0x89);
    }

    [Fact]
    public void FromTextSkipsNonProgramLines()
    {
        var prg = PrgFormat.FromText(new[] { "REM header", "", "10 PRINT 1", "  20   END" });
        var lines = PrgFormat.Detokenize(prg);
        Assert.Equal(new (int, string)[] { (10, "PRINT 1"), (20, "END") }, lines);
    }

    [Fact]
    public void MalformedProgramsDoNotCrash()
    {
        Assert.Empty(PrgFormat.Detokenize(new byte[] { 1 }));
        Assert.Empty(PrgFormat.Detokenize(new byte[] { 1, 8, 0, 0 }));
        PrgFormat.Detokenize(new byte[] { 1, 8, 9, 8, 10, 0, 0x99 }); // no terminator
    }
}

public class D64ImageTests
{
    static D64Image Blank() => D64Image.Create("TEST DISK", "01");

    static byte[] Bytes(int count, int seed = 1) => Enumerable.Range(0, count).Select(i => (byte)((i * 7 + seed) & 0xFF)).ToArray();

    [Fact]
    public void BlankDiskHasTheRealLayout()
    {
        var disk = Blank();
        Assert.Equal("TEST DISK", disk.Title);
        Assert.Equal("01", disk.Id);
        Assert.Equal(664, disk.BlocksFree);
        Assert.Empty(disk.Directory());

        var image = disk.ToArray();
        Assert.Equal(D64Image.Size, image.Length);
        int bam = 357 * 256;                    // track 18 sector 0
        Assert.Equal(18, image[bam]);
        Assert.Equal(1, image[bam + 1]);
        Assert.Equal(0x41, image[bam + 2]);
        Assert.Equal((byte)'T', image[bam + 0x90]);
        Assert.Equal(0xA0, image[bam + 0x90 + 9]);
        Assert.Equal(21, image[bam + 4]);       // track 1 has 21 free sectors
        Assert.Equal(17, image[bam + 4 * 35]);  // track 35 has 17
        Assert.Equal(17, image[bam + 4 * 18]);  // track 18: 19 minus the BAM and first directory sector
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(253)]
    [InlineData(254)]
    [InlineData(255)]
    [InlineData(508)]
    [InlineData(1000)]
    [InlineData(20000)]
    public void FilesRoundTrip(int length)
    {
        var disk = Blank();
        var data = Bytes(length);
        disk.Write("FILE", FileType.Prg, data, false);
        var back = disk.Read("FILE");
        Assert.Equal(data, back.Data);
        Assert.Equal(FileType.Prg, back.Type);
        Assert.Equal("FILE", back.Name);
        int blocks = Math.Max(1, (length + 253) / 254);
        Assert.Equal(664 - blocks, disk.BlocksFree);
        Assert.Equal(blocks, disk.Directory().Single().Blocks);
    }

    [Fact]
    public void FileSectorsFollowTheInterleave()
    {
        var disk = Blank();
        disk.Write("A", FileType.Prg, Bytes(254 * 3), false);
        var image = disk.ToArray();
        int dir = 357 * 256 + 256;              // 18/1
        int track = image[dir + 3], sector = image[dir + 4];
        Assert.Equal(17, track);                // the track nearest the directory
        Assert.Equal(0, sector);
        int at = 16 * 21 * 256;                 // track 17 starts after 16 tracks of 21 sectors
        Assert.Equal(17, image[at]);
        Assert.Equal(10, image[at + 1]);        // interleave of 10
    }

    [Fact]
    public void DirectoryEntriesHaveTypesAndNames()
    {
        IDiskDrive disk = Blank();
        disk.Write("PROGRAM", FileType.Prg, Bytes(10), false);
        disk.WriteText("DATA", "ONE\rTWO\r", false);
        var dir = disk.Directory();
        Assert.Equal(new[] { "PROGRAM", "DATA" }, dir.Select(e => e.Name));
        Assert.Equal(new[] { FileType.Prg, FileType.Seq }, dir.Select(e => e.Type));
        Assert.Equal("ONE\rTWO\r", disk.ReadText("DATA"));
    }

    [Fact]
    public void ExistingNamesNeedReplace()
    {
        var disk = Blank();
        disk.Write("X", FileType.Prg, Bytes(10), false);
        var e = Assert.Throws<DriveException>(() => disk.Write("X", FileType.Prg, Bytes(5), false));
        Assert.Equal(63, e.Code);
        disk.Write("X", FileType.Prg, Bytes(5, 9), true);
        Assert.Equal(Bytes(5, 9), disk.Read("X").Data);
        Assert.Single(disk.Directory());
        Assert.Equal(663, disk.BlocksFree);
    }

    [Fact]
    public void MissingFileIsError62()
    {
        Assert.Equal(62, Assert.Throws<DriveException>(() => Blank().Read("NOPE")).Code);
    }

    [Fact]
    public void WildcardsMatchNames()
    {
        var disk = Blank();
        disk.Write("GAME1", FileType.Prg, Bytes(3), false);
        disk.Write("GAME2", FileType.Prg, Bytes(3, 2), false);
        disk.Write("OTHER", FileType.Prg, Bytes(3, 3), false);
        Assert.Equal("GAME1", disk.Read("GA*").Name);
        Assert.Equal("GAME1", disk.Read("GAME?").Name);
        Assert.Equal("OTHER", disk.Read("O?H*").Name);
        Assert.Equal("GAME1", disk.Read("*").Name);
        Assert.Equal(2, disk.Scratch("GAME?"));
        Assert.Equal(new[] { "OTHER" }, disk.Directory().Select(e => e.Name));
    }

    [Fact]
    public void ScratchFreesTheBlocks()
    {
        var disk = Blank();
        disk.Write("BIG", FileType.Prg, Bytes(5000), false);
        Assert.True(disk.BlocksFree < 664 - 19);
        Assert.Equal(1, disk.Scratch("BIG"));
        Assert.Equal(664, disk.BlocksFree);
        Assert.Empty(disk.Directory());
        Assert.Equal(0, disk.Scratch("BIG"));
    }

    [Fact]
    public void ScratchedDirectorySlotsAreReused()
    {
        var disk = Blank();
        disk.Write("A", FileType.Prg, Bytes(3), false);
        disk.Write("B", FileType.Prg, Bytes(3), false);
        disk.Scratch("A");
        disk.Write("C", FileType.Prg, Bytes(3), false);
        Assert.Equal(new[] { "C", "B" }, disk.Directory().Select(e => e.Name));
    }

    [Fact]
    public void RenameChangesTheName()
    {
        var disk = Blank();
        disk.Write("OLD", FileType.Prg, Bytes(3), false);
        disk.Write("TAKEN", FileType.Prg, Bytes(3), false);
        disk.Rename("OLD", "NEW");
        Assert.Equal("NEW", disk.Read("NEW").Name);
        Assert.Equal(63, Assert.Throws<DriveException>(() => disk.Rename("NEW", "TAKEN")).Code);
        Assert.Equal(62, Assert.Throws<DriveException>(() => disk.Rename("GONE", "X")).Code);
    }

    [Fact]
    public void DirectoryGrowsPastOneSector()
    {
        var disk = Blank();
        for (int i = 0; i < 20; i++) disk.Write($"FILE{i:00}", FileType.Prg, Bytes(3, i), false);
        Assert.Equal(20, disk.Directory().Count);
        for (int i = 0; i < 20; i++) Assert.Equal(Bytes(3, i), disk.Read($"FILE{i:00}").Data);
        Assert.Equal(664 - 20, disk.BlocksFree); // the extra directory sectors are on track 18
    }

    [Fact]
    public void FullDiskIsError72AndLeavesTheDiskIntact()
    {
        var disk = Blank();
        disk.Write("KEEP", FileType.Prg, Bytes(100), false);
        int free = disk.BlocksFree;
        var e = Assert.Throws<DriveException>(() => disk.Write("HUGE", FileType.Prg, Bytes(254 * 700), false));
        Assert.Equal(72, e.Code);
        Assert.Equal(free, disk.BlocksFree);
        Assert.Equal(Bytes(100), disk.Read("KEEP").Data);
        disk.Write("FILL", FileType.Prg, Bytes(254 * free), false);
        Assert.Equal(0, disk.BlocksFree);
        Assert.Equal(72, Assert.Throws<DriveException>(() => disk.Write("MORE", FileType.Prg, Bytes(1), false)).Code);
    }

    [Theory]
    [InlineData("")]
    [InlineData("A,B")]
    [InlineData("A*")]
    [InlineData("THIS NAME IS FAR TOO LONG")]
    public void BadNamesAreRejected(string name)
    {
        var e = Assert.Throws<DriveException>(() => Blank().Write(name, FileType.Prg, Bytes(1), false));
        Assert.Contains(e.Code, new[] { 33, 34 });
    }

    [Fact]
    public void ValidateRebuildsTheAllocationMap()
    {
        var disk = Blank();
        disk.Write("A", FileType.Prg, Bytes(3000), false);
        disk.Write("B", FileType.Prg, Bytes(700), false);
        int free = disk.BlocksFree;
        var image = disk.ToArray();
        for (int i = 4; i < 4 * 36; i++) image[357 * 256 + i] = 0;  // wreck the BAM
        var broken = new D64Image(image);
        Assert.NotEqual(free, broken.BlocksFree);
        broken.Validate();
        Assert.Equal(free, broken.BlocksFree);
        Assert.Equal(Bytes(3000), broken.Read("A").Data);
    }

    [Fact]
    public void FormatErasesEverything()
    {
        var disk = Blank();
        disk.Write("A", FileType.Prg, Bytes(300), false);
        disk.Format("NEWNAME", "AB");
        Assert.Empty(disk.Directory());
        Assert.Equal("NEWNAME", disk.Title);
        Assert.Equal("AB", disk.Id);
        Assert.Equal(664, disk.BlocksFree);
    }

    [Fact]
    public void ChangesAreReportedForSaving()
    {
        byte[]? saved = null;
        var disk = D64Image.Create("X", "00", b => saved = (byte[])b.Clone());
        disk.Write("A", FileType.Prg, Bytes(300), false);
        Assert.NotNull(saved);
        var reopened = new D64Image(saved!);
        Assert.Equal(Bytes(300), reopened.Read("A").Data);
    }

    [Fact]
    public void ShortImagesAreRejected() =>
        Assert.Throws<InvalidDataException>(() => new D64Image(new byte[100]));
}

public class T64ImageTests
{
    [Fact]
    public void WritesAndReadsBack()
    {
        byte[]? saved = null;
        var tape = new T64Image(null, "MY TAPE", b => saved = b);
        var prg = new byte[] { 0x01, 0x08, 1, 2, 3, 4 };
        tape.Write("PROG", FileType.Prg, prg, false);
        Assert.NotNull(saved);
        Assert.Equal("C64S tape image file", System.Text.Encoding.ASCII.GetString(saved!, 0, 20));

        var reopened = new T64Image(saved);
        Assert.Equal("MY TAPE", reopened.Title);
        Assert.Equal(prg, reopened.Read("PROG").Data);
        Assert.Equal("PROG", reopened.Directory().Single().Name);
    }

    [Fact]
    public void EmptyNameReadsTheFirstProgram()
    {
        var tape = new T64Image();
        tape.Write("ONE", FileType.Prg, new byte[] { 0, 8, 1 }, false);
        tape.Write("TWO", FileType.Prg, new byte[] { 0, 8, 2 }, false);
        Assert.Equal("ONE", tape.Read("").Name);
        Assert.Equal("TWO", tape.Read("T*").Name);
    }

    [Fact]
    public void OnlyProgramsFitOnTape()
    {
        var tape = new T64Image();
        Assert.Equal(64, Assert.Throws<DriveException>(() => tape.Write("S", FileType.Seq, new byte[1], false)).Code);
        tape.Write("P", FileType.Prg, new byte[] { 0, 8 }, false);
        Assert.Equal(63, Assert.Throws<DriveException>(() => tape.Write("P", FileType.Prg, new byte[] { 0, 8 }, false)).Code);
        Assert.Equal(1, tape.Scratch("P"));
    }

    [Fact]
    public void BadImagesAreRejected() =>
        Assert.Throws<InvalidDataException>(() => new T64Image(new byte[200]));
}

public class HostDirectoryDriveTests
{
    [Fact]
    public void ProgramsAreSavedAsSourceUnlessNamedPrg()
    {
        var fs = new MemoryFileSystem();
        var drive = new HostDirectoryDrive(fs);
        drive.WriteProgram("GAME", new byte[] { 1, 8, 0, 0 }, new[] { "10 END" }, true);
        drive.WriteProgram("BIN.prg", new byte[] { 1, 8, 0, 0 }, new[] { "10 END" }, true);
        Assert.Equal(new[] { "10 END" }, fs.Files["GAME.bas"]);
        Assert.Equal(new byte[] { 1, 8, 0, 0 }, fs.Binary["BIN.prg"]);
    }

    [Fact]
    public void ReadFindsExtensionsAndTokenizesSource()
    {
        var fs = new MemoryFileSystem();
        fs.Files["a.bas"] = new[] { "10 PRINT 1" };
        fs.Binary["b.prg"] = new byte[] { 0, 192, 96 };
        var drive = new HostDirectoryDrive(fs);
        Assert.Equal(PrgFormat.FromText(new[] { "10 PRINT 1" }), drive.Read("a").Data);
        Assert.Equal(new byte[] { 0, 192, 96 }, drive.Read("b").Data);
        Assert.Equal(new[] { "10 PRINT 1" }, drive.ReadProgramText("a"));
        Assert.Null(drive.ReadProgramText("b"));
        Assert.Equal(62, Assert.Throws<DriveException>(() => drive.Read("c")).Code);
    }

    [Fact]
    public void DirectoryShowsHostFiles()
    {
        var fs = new MemoryFileSystem();
        fs.Files["prog.bas"] = new[] { "10 END" };
        fs.Files["notes"] = new[] { "hello" };
        var dir = new HostDirectoryDrive(fs).Directory();
        Assert.Contains(dir, e => e.Name == "PROG" && e.Type == FileType.Prg);
        Assert.Contains(dir, e => e.Name == "NOTES" && e.Type == FileType.Seq);
    }

    [Fact]
    public void ScratchAndRenameWork()
    {
        var fs = new MemoryFileSystem();
        fs.Files["a.bas"] = new[] { "10 END" };
        fs.Files["b.bas"] = new[] { "20 END" };
        var drive = new HostDirectoryDrive(fs);
        Assert.Equal(1, drive.Scratch("a"));
        Assert.False(fs.Exists("a.bas"));
        drive.Rename("b", "c");
        Assert.True(fs.Exists("c.bas"));
        Assert.False(fs.Exists("b.bas"));
    }

    [Fact]
    public void ADirectoryCannotBeFormatted() =>
        Assert.Equal(26, Assert.Throws<DriveException>(() => new HostDirectoryDrive(new MemoryFileSystem()).Format("X", null)).Code);
}
