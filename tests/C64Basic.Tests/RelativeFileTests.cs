using C64Basic.Core.Disk;
using C64Basic.Core.Runtime;

namespace C64Basic.Tests;

public class RelativeFileTests
{
    static byte[] Record(int length, params byte[] start)
    {
        var r = new byte[length];
        start.CopyTo(r, 0);
        return r;
    }

    // ---------- the disk image ----------
    [Fact]
    public void CreatingARelativeFileMakesADataBlockAndASideSector()
    {
        var disk = D64Image.Create("X", "00");
        var rel = ((IDiskDrive)disk).OpenRel("REL", 30)!;
        Assert.Equal(30, rel.RecordLength);
        Assert.Equal(1, rel.RecordCount);

        var entry = disk.Directory().Single();
        Assert.Equal(("REL", FileType.Rel, 2), (entry.Name, entry.Type, entry.Blocks));
        Assert.Equal(662, disk.BlocksFree);
    }

    [Fact]
    public void TheSideSectorHasTheRealLayout()
    {
        var disk = D64Image.Create("X", "00");
        ((IDiskDrive)disk).OpenRel("REL", 30);
        var image = disk.ToArray();
        int dir = 357 * 256 + 256;                          // the first directory sector
        Assert.Equal(0x84, image[dir + 2]);
        int dataTrack = image[dir + 3], dataSector = image[dir + 4];
        int sideTrack = image[dir + 21], sideSector = image[dir + 22];
        Assert.Equal(30, image[dir + 23]);

        int Offset(int t, int s) => (Enumerable.Range(1, t - 1).Sum(D64Image.SectorsIn) + s) * 256;
        int side = Offset(sideTrack, sideSector);
        Assert.Equal(0, image[side]);                       // the only side sector
        Assert.Equal(15 + 2, image[side + 1]);              // last used byte: one block listed
        Assert.Equal(0, image[side + 2]);                   // side sector number 0
        Assert.Equal(30, image[side + 3]);                  // the record length again
        Assert.Equal((sideTrack, sideSector), (image[side + 4], image[side + 5]));   // lists itself
        Assert.Equal((dataTrack, dataSector), (image[side + 16], image[side + 17])); // then its data block
        int data = Offset(dataTrack, dataSector);
        Assert.Equal(0, image[data]);
        Assert.Equal(1 + 30, image[data + 1]);              // one 30-byte record
        Assert.Equal(0xFF, image[data + 2]);                // an empty record starts with $FF
    }

    [Fact]
    public void RecordsRoundTripAndTheFileGrows()
    {
        IDiskDrive disk = D64Image.Create("X", "00");
        var rel = disk.OpenRel("REL", 20)!;
        rel.Write(0, Record(20, 1, 2, 3));
        rel.Write(4, Record(20, 9, 9));
        Assert.Equal(5, rel.RecordCount);
        Assert.Equal(Record(20, 1, 2, 3), rel.Read(0));
        Assert.Equal(Record(20, 9, 9), rel.Read(4));
        Assert.Equal(Record(20, 0xFF), rel.Read(2));         // skipped records are empty
        Assert.Equal(50, Assert.Throws<DriveException>(() => rel.Read(5)).Code);
    }

    [Fact]
    public void RecordsMaySpanBlocks()
    {
        IDiskDrive disk = D64Image.Create("X", "00");
        var rel = disk.OpenRel("REL", 100)!;
        for (int r = 0; r < 10; r++) rel.Write(r, Record(100, (byte)(r + 1), (byte)(r + 1)));
        Assert.Equal(10, rel.RecordCount);
        for (int r = 0; r < 10; r++) Assert.Equal(Record(100, (byte)(r + 1), (byte)(r + 1)), rel.Read(r));
        Assert.Equal(5, disk.Directory().Single().Blocks);                        // 4 data blocks + 1 side sector
    }

    [Fact]
    public void BigFilesNeedMoreSideSectors()
    {
        var disk = D64Image.Create("X", "00");
        IDiskDrive drive = disk;
        var rel = drive.OpenRel("BIG", 254)!;
        rel.Write(129, Record(254, 7));                        // 130 records = 130 data blocks
        Assert.Equal(130, rel.RecordCount);
        Assert.Equal(130 + 2, drive.Directory().Single().Blocks);   // two side sectors
        Assert.Equal(Record(254, 7), rel.Read(129));
        Assert.Equal(Record(254, 0xFF), rel.Read(120));

        var image = disk.ToArray();
        int dir = 357 * 256 + 256;
        int Offset(int t, int s) => (Enumerable.Range(1, t - 1).Sum(D64Image.SectorsIn) + s) * 256;
        int first = Offset(image[dir + 21], image[dir + 22]);
        Assert.NotEqual(0, image[first]);                       // chained to the second side sector
        int second = Offset(image[first], image[first + 1]);
        Assert.Equal(1, image[second + 2]);                     // side sector number 1
        Assert.Equal(0, image[second]);
        Assert.Equal(15 + 2 * 10, image[second + 1]);           // the last ten blocks
        Assert.Equal((image[first + 4], image[first + 5]), (image[second + 4], image[second + 5]));   // both headers list the group
        Assert.Equal(image[first + 6], image[first + 6]);
    }

    [Fact]
    public void FilesLargerThanSixSideSectorsAreRefused()
    {
        IDiskDrive disk = D64Image.Create("X", "00");
        var rel = disk.OpenRel("HUGE", 254)!;
        Assert.Equal(52, Assert.Throws<DriveException>(() => rel.Write(720, Record(254))).Code);
    }

    [Fact]
    public void AFullDiskStopsGrowingTheFile()
    {
        IDiskDrive disk = D64Image.Create("X", "00");
        var rel = disk.OpenRel("R", 254)!;
        disk.Write("FILL", FileType.Prg, new byte[254 * ((D64Image)disk).BlocksFree], false);
        Assert.Equal(72, Assert.Throws<DriveException>(() => rel.Write(5, Record(254))).Code);
        Assert.Equal(1, rel.RecordCount);
    }

    [Fact]
    public void ScratchingFreesDataAndSideSectors()
    {
        var disk = D64Image.Create("X", "00");
        IDiskDrive drive = disk;
        var rel = drive.OpenRel("REL", 100)!;
        rel.Write(20, Record(100, 5));
        Assert.True(disk.BlocksFree < 660);
        Assert.Equal(1, drive.Scratch("REL"));
        Assert.Equal(664, disk.BlocksFree);
    }

    [Fact]
    public void ValidateKeepsASideSectorsMarkedUsed()
    {
        var disk = D64Image.Create("X", "00");
        IDiskDrive drive = disk;
        drive.OpenRel("REL", 100)!.Write(10, Record(100, 5));
        int free = disk.BlocksFree;
        var image = disk.ToArray();
        for (int i = 4; i < 4 * 36; i++) image[357 * 256 + i] = 0;
        var broken = new D64Image(image);
        broken.Validate();
        Assert.Equal(free, broken.BlocksFree);
    }

    [Fact]
    public void ReopeningFindsTheSameFile()
    {
        IDiskDrive disk = D64Image.Create("X", "00");
        disk.OpenRel("REL", 20)!.Write(3, Record(20, 4));
        var again = disk.OpenRel("REL", 0)!;
        Assert.Equal(20, again.RecordLength);
        Assert.Equal(4, again.RecordCount);
        Assert.Equal(Record(20, 4), again.Read(3));
    }

    [Fact]
    public void OrdinaryFilesAreNotRelative()
    {
        IDiskDrive disk = D64Image.Create("X", "00");
        disk.Write("PRG", FileType.Prg, new byte[] { 1, 8, 0, 0 }, false);
        Assert.Null(disk.OpenRel("PRG", 0));
        Assert.Equal(64, Assert.Throws<DriveException>(() => disk.OpenRel("PRG", 20)).Code);
        Assert.Null(disk.OpenRel("MISSING", 0));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(255)]
    public void RecordLengthsMustFitABlock(int length)
    {
        IDiskDrive disk = D64Image.Create("X", "00");
        if (length == 0) { Assert.Null(disk.OpenRel("R", 0)); return; }
        Assert.Equal(30, Assert.Throws<DriveException>(() => disk.OpenRel("R", length)).Code);
    }

    [Fact]
    public void HostDirectoriesAndTapesHaveNoRelativeFiles()
    {
        IDiskDrive host = new HostDirectoryDrive(new MemoryFileSystem());
        Assert.Equal(64, Assert.Throws<DriveException>(() => host.OpenRel("X", 10)).Code);
        Assert.Null(host.OpenRel("X", 0));
    }

    // ---------- from BASIC ----------
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

        public void Run(params string[] lines) { foreach (var l in lines) Interp.ProcessLine(l); }
        public string Output => Console.Output;
        public IRelFile Rel(string name) => ((IDiskDrive)Disk).OpenRel(name, 0)!;
    }

    const string Open = "OPEN 2,8,2,\"REL,L,\"+CHR$(20):OPEN 15,8,15";
    static string Pos(int record, int position = 1) => $"PRINT#15,\"P\"+CHR$(98)+CHR$({record & 255})+CHR$({record >> 8})+CHR$({position})";

    [Fact]
    public void WritingRecordsFromBasic()
    {
        var s = new Session();
        s.Run(Open, "PRINT#2,\"HELLO\"", Pos(3), "PRINT#2,\"WORLD\"", "CLOSE 2:CLOSE 15");
        var rel = s.Rel("REL");
        Assert.Equal(3, rel.RecordCount);                                  // record 3 is the third one
        Assert.Equal(Record(20, 72, 69, 76, 76, 79, 13), rel.Read(0));      // HELLO, CR, then nulls
        Assert.Equal(Record(20, 0xFF), rel.Read(1));
        Assert.Equal(Record(20, 87, 79, 82, 76, 68, 13), rel.Read(2));
    }

    [Fact]
    public void ReadingRecordsBackInSequence()
    {
        var s = new Session();
        s.Run(Open, "PRINT#2,\"ONE\"", "PRINT#2,\"TWO\"", "PRINT#2,\"THREE\"", Pos(1), "INPUT#2,A$,B$,C$:PRINT A$;B$;C$");
        Assert.EndsWith("ONETWOTHREE\n", s.Output);
    }

    [Fact]
    public void PositioningPicksARecord()
    {
        var s = new Session();
        s.Run(Open, "FOR I=1 TO 5:PRINT#2,CHR$(64+I):NEXT", Pos(4), "INPUT#2,A$:PRINT A$", Pos(2), "INPUT#2,A$:PRINT A$");
        Assert.EndsWith("D\nB\n", s.Output);
    }

    [Fact]
    public void PositioningPastTheEndReportsError50()
    {
        var s = new Session();
        s.Run(Open, Pos(50), "INPUT#15,E:PRINT E");
        Assert.EndsWith(" 50 \n", s.Output);
    }

    [Fact]
    public void WritingPastTheEndGrowsTheFileWithEmptyRecords()
    {
        var s = new Session();
        s.Run(Open, Pos(10), "PRINT#2,\"TEN\"", Pos(5), "INPUT#2,A$:PRINT LEN(A$)");
        Assert.EndsWith(" 0 \n", s.Output);                       // record 5 exists and is empty
        Assert.Equal(10, s.Rel("REL").RecordCount);
    }

    [Fact]
    public void ARecordThatIsTooLongOverflows()
    {
        var s = new Session();
        s.Run(Open, "PRINT#2,\"THIS IS FAR TOO LONG FOR TWENTY\"", "INPUT#15,E:PRINT E");
        Assert.EndsWith(" 51 \n", s.Output);
        s.Run("CLOSE 2:CLOSE 15");
        Assert.Equal(Record(20, 84, 72, 73, 83, 32, 73, 83, 32, 70, 65, 82, 32, 84, 79, 79, 32, 76, 79, 78, 71)[..20], s.Rel("REL").Read(0));
    }

    [Fact]
    public void WritingInTheMiddleOfARecordKeepsTheStart()
    {
        var s = new Session();
        s.Run(Open, "PRINT#2,\"ABCDEFG\"", Pos(1, 3), "PRINT#2,\"XY\"", "CLOSE 2:CLOSE 15");
        Assert.Equal(Record(20, 65, 66, 88, 89, 13), s.Rel("REL").Read(0));     // AB, XY, CR; the rest is nulled
    }

    [Fact]
    public void RecordNumbersThatLookLikeACarriageReturnWork()
    {
        var s = new Session();
        s.Run(Open, Pos(13), "PRINT#2,\"THIRTEEN\"", Pos(13), "INPUT#2,A$:PRINT A$");
        Assert.EndsWith("THIRTEEN\n", s.Output);
        Assert.Equal(13, s.Rel("REL").RecordCount);
    }

    [Fact]
    public void RecordNumbersAboveTwoFiftySixUseTheHighByte()
    {
        var s = new Session();
        s.Run(Open, Pos(300), "PRINT#2,\"X\"", "CLOSE 2:CLOSE 15");
        Assert.Equal(300, s.Rel("REL").RecordCount);
    }

    [Fact]
    public void AnUnterminatedWriteIsKeptWhenTheFileCloses()
    {
        var s = new Session();
        s.Run(Open, "PRINT#2,\"PARTIAL\";", "CLOSE 2:CLOSE 15");
        Assert.Equal(Record(20, 80, 65, 82, 84, 73, 65, 76), s.Rel("REL").Read(0));
    }

    [Fact]
    public void ExistingRelativeFilesOpenWithoutTheLength()
    {
        var s = new Session();
        s.Run(Open, "PRINT#2,\"KEEP\"", "CLOSE 2");
        s.Run("OPEN 3,8,3,\"REL\"", Pos(1).Replace("CHR$(98)", "CHR$(99)"), "INPUT#3,A$:PRINT A$");
        Assert.EndsWith("KEEP\n", s.Output);
    }

    [Fact]
    public void GetReadsTheBytesOfARecord()
    {
        var s = new Session();
        s.Run(Open, "PRINT#2,\"AB\"", Pos(1), "GET#2,A$:GET#2,B$:GET#2,C$:PRINT A$;B$;ASC(C$)");
        Assert.EndsWith("AB 13 \n", s.Output);
    }

    [Fact]
    public void ChannelsOfDifferentFilesKeepTheirOwnPositions()
    {
        var s = new Session();
        s.Run(Open, "OPEN 3,8,3,\"OTHER,L,\"+CHR$(20)");
        s.Run("PRINT#2,\"A\"", "PRINT#3,\"B\"", "PRINT#2,\"C\"");
        s.Run("CLOSE 2:CLOSE 3:CLOSE 15");
        Assert.Equal(67, s.Rel("REL").Read(1)[0]);
        Assert.Equal(66, s.Rel("OTHER").Read(0)[0]);
    }

    [Fact]
    public void ARelativeFileOnAHostDirectoryReportsError64()
    {
        var console = new TestConsole();
        var interp = new Interpreter(console, new MemoryFileSystem());
        interp.ProcessLine("OPEN 15,8,15:OPEN 2,8,2,\"X,L,\"+CHR$(10)");
        interp.ProcessLine("INPUT#15,E:PRINT E");
        Assert.EndsWith(" 64 \n", console.Output);
    }

    [Fact]
    public void ARelativeFileShowsInTheDirectoryAsREL()
    {
        var s = new Session();
        s.Run(Open, "PRINT#2,\"X\"", "CLOSE 2:CLOSE 15", "LOAD \"$\",8");
        Assert.Contains(s.Interp.Listing(), l => l.Contains("\"REL\"") && l.EndsWith("REL"));
    }
}
