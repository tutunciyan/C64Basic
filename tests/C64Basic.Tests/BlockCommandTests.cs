using C64Basic.Core.Disk;
using C64Basic.Core.Runtime;

namespace C64Basic.Tests;

public class BlockCommandTests
{
    sealed class Session
    {
        public readonly TestConsole Console = new();
        public readonly D64Image Disk = D64Image.Create("TEST", "01");
        public readonly Interpreter Interp;

        public Session(params string[] lines)
        {
            Interp = new Interpreter(Console, new MemoryFileSystem());
            Interp.MountDrive(8, Disk);
            Run(lines);
        }

        public void Run(params string[] lines) { foreach (var l in lines) Interp.ProcessLine(l); }
        public string Output => Console.Output;
    }

    static Session WithChannels(params string[] more)
    {
        var s = new Session("OPEN 5,8,5,\"#\"", "OPEN 15,8,15");
        s.Run(more);
        return s;
    }

    // ---------- the disk image ----------
    [Fact]
    public void BlocksCanBeReadAndWritten()
    {
        IDiskDrive disk = D64Image.Create("X", "00");
        var block = new byte[256];
        block[0] = 7; block[255] = 9;
        disk.WriteBlock(1, 0, block);
        Assert.Equal(block, disk.ReadBlock(1, 0));
        Assert.Equal(18, disk.ReadBlock(18, 0)[0]);      // the BAM sector links to the directory
    }

    [Fact]
    public void ShortWritesAreZeroPadded()
    {
        IDiskDrive disk = D64Image.Create("X", "00");
        disk.WriteBlock(2, 3, new byte[] { 1, 2, 3 });
        var back = disk.ReadBlock(2, 3);
        Assert.Equal(new byte[] { 1, 2, 3, 0 }, back[..4]);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(36, 0)]
    [InlineData(1, 21)]
    [InlineData(18, 19)]
    public void IllegalTracksAndSectorsAreError66(int track, int sector)
    {
        IDiskDrive disk = D64Image.Create("X", "00");
        Assert.Equal(66, Assert.Throws<DriveException>(() => disk.ReadBlock(track, sector)).Code);
    }

    [Fact]
    public void AllocateAndFreeUpdateTheMap()
    {
        var disk = D64Image.Create("X", "00");
        IDiskDrive drive = disk;
        int free = disk.BlocksFree;
        drive.AllocateBlock(5, 5);
        Assert.Equal(free - 1, disk.BlocksFree);
        var e = Assert.Throws<DriveException>(() => drive.AllocateBlock(5, 5));
        Assert.Equal(65, e.Code);
        Assert.Equal((5, 6), (e.Track, e.Sector));        // the next free block
        drive.FreeBlock(5, 5);
        Assert.Equal(free, disk.BlocksFree);
    }

    [Fact]
    public void AllocatingTheLastFreeBlockOfATrackSuggestsTheNextTrack()
    {
        var disk = D64Image.Create("X", "00");
        IDiskDrive drive = disk;
        for (int s = 0; s < 21; s++) drive.AllocateBlock(1, s);
        var e = Assert.Throws<DriveException>(() => drive.AllocateBlock(1, 20));
        Assert.Equal((2, 0), (e.Track, e.Sector));
    }

    [Fact]
    public void HostDirectoriesAndTapesHaveNoBlocks()
    {
        IDiskDrive host = new HostDirectoryDrive(new MemoryFileSystem());
        Assert.Equal(66, Assert.Throws<DriveException>(() => host.ReadBlock(1, 0)).Code);
        IDiskDrive tape = new T64Image();
        Assert.Equal(66, Assert.Throws<DriveException>(() => tape.AllocateBlock(1, 0)).Code);
    }

    // ---------- from BASIC ----------
    [Fact]
    public void U1ReadsABlockIntoTheBuffer()
    {
        var s = WithChannels("PRINT#15,\"U1:5 0 18 0\"", "GET#5,A$:GET#5,B$:PRINT ASC(A$);ASC(B$)");
        Assert.EndsWith(" 18  1 \n", s.Output);             // the BAM block starts 18, 1
    }

    [Fact]
    public void ArgumentsMayBeSeparatedByCommasAndTheOldNamesWork()
    {
        var s = WithChannels("PRINT#15,\"U1: 5,0,18,0\"", "GET#5,A$:PRINT ASC(A$)");
        Assert.EndsWith(" 18 \n", s.Output);
        s.Run("PRINT#15,\"B-R\";5;0;18;0", "GET#5,A$:PRINT ASC(A$)");
        Assert.EndsWith(" 18 \n", s.Output);
        s.Run("PRINT#15,\"UA:5 0 18 0\"", "GET#5,A$:PRINT ASC(A$)");
        Assert.EndsWith(" 18 \n", s.Output);
    }

    [Fact]
    public void BPositionsTheBufferPointer()
    {
        var s = WithChannels("PRINT#15,\"U1:5 0 18 0\"", "PRINT#15,\"B-P:5 144\"", "GET#5,A$:PRINT A$");
        Assert.EndsWith("T\n", s.Output);                   // the disk name starts at byte 144
    }

    [Fact]
    public void U2WritesTheBufferBack()
    {
        var s = WithChannels("PRINT#5,\"AB\"", "PRINT#15,\"U2:5 0 1 0\"");
        var block = ((IDiskDrive)s.Disk).ReadBlock(1, 0);
        Assert.Equal(new byte[] { 65, 66, 13, 0 }, block[..4]);
    }

    [Fact]
    public void BufferPointerStartsAtZeroAndBPRewinds()
    {
        var s = WithChannels("PRINT#5,\"XY\"", "PRINT#15,\"B-P:5 0\"", "PRINT#5,\"Z\"", "PRINT#15,\"U2:5 0 1 0\"");
        Assert.Equal(new byte[] { 90, 13, 13 }, ((IDiskDrive)s.Disk).ReadBlock(1, 0)[..3]);   // Z, CR, then the old CR
    }

    [Fact]
    public void BAAllocatesAndReportsTheNextFreeBlock()
    {
        var s = WithChannels("PRINT#15,\"B-A:0 10 3\"");
        Assert.Equal(663, s.Disk.BlocksFree);
        s.Run("PRINT#15,\"B-A:0 10 3\"", "INPUT#15,E,E$,T,S:PRINT E;T;S");
        Assert.EndsWith(" 65  10  4 \n", s.Output);
        s.Run("PRINT#15,\"B-F:0 10 3\"");
        Assert.Equal(664, s.Disk.BlocksFree);
    }

    [Fact]
    public void ErrorsAreReportedOnTheErrorChannel()
    {
        var s = WithChannels("PRINT#15,\"U1:5 0 36 0\"", "INPUT#15,E:PRINT E");
        Assert.EndsWith(" 66 \n", s.Output);
        s.Run("PRINT#15,\"U1:9 0 18 0\"", "INPUT#15,E:PRINT E");           // channel 9 is not open
        Assert.EndsWith(" 70 \n", s.Output);
        s.Run("PRINT#15,\"U1:5 0\"", "INPUT#15,E:PRINT E");                // missing arguments
        Assert.EndsWith(" 30 \n", s.Output);
        s.Run("PRINT#15,\"B-P:5 300\"", "INPUT#15,E:PRINT E");
        Assert.EndsWith(" 30 \n", s.Output);
    }

    [Fact]
    public void TheBufferEndsAfter256Bytes()
    {
        var s = WithChannels("PRINT#15,\"B-P:5 255\"", "GET#5,A$:PRINT ST");
        Assert.EndsWith(" 64 \n", s.Output);
        s.Run("GET#5,A$:PRINT ST");
        Assert.EndsWith(" 64 \n", s.Output);                              // still the end
    }

    [Fact]
    public void WritingPastTheEndOfTheBufferIsIgnored()
    {
        var s = WithChannels("PRINT#15,\"B-P:5 254\"", "PRINT#5,\"ABCDEF\"", "PRINT#15,\"U2:5 0 1 0\"");
        var block = ((IDiskDrive)s.Disk).ReadBlock(1, 0);
        Assert.Equal(new byte[] { 65, 66 }, block[254..]);
    }

    [Fact]
    public void ChannelsKeepSeparateBuffers()
    {
        var s = new Session("OPEN 5,8,5,\"#\"", "OPEN 6,8,6,\"#\"", "OPEN 15,8,15");
        s.Run("PRINT#5,\"A\"", "PRINT#6,\"B\"", "PRINT#15,\"U2:5 0 1 0\"", "PRINT#15,\"U2:6 0 2 0\"");
        Assert.Equal(65, ((IDiskDrive)s.Disk).ReadBlock(1, 0)[0]);
        Assert.Equal(66, ((IDiskDrive)s.Disk).ReadBlock(2, 0)[0]);
    }

    [Fact]
    public void ClosingAChannelRemovesItsBuffer()
    {
        var s = WithChannels("CLOSE 5", "PRINT#15,\"U1:5 0 18 0\"", "INPUT#15,E:PRINT E");
        Assert.EndsWith(" 70 \n", s.Output);
    }

    [Fact]
    public void BlockCommandsOnAHostDirectoryAreRefused()
    {
        var console = new TestConsole();
        var interp = new Interpreter(console, new MemoryFileSystem());
        interp.ProcessLine("OPEN 5,8,5,\"#\":OPEN 15,8,15");
        interp.ProcessLine("PRINT#15,\"U1:5 0 18 0\"");
        interp.ProcessLine("INPUT#15,E:PRINT E");
        Assert.EndsWith(" 66 \n", console.Output);
    }

    [Fact]
    public void AReadFileCannotBeWrittenAndABufferCanBeBoth()
    {
        var s = WithChannels("PRINT#5,\"Q\"", "PRINT#15,\"B-P:5 0\"", "GET#5,A$:PRINT A$");
        Assert.EndsWith("Q\n", s.Output);
    }
}
