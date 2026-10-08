using C64Basic.Core.Disk;

namespace C64Basic.Tests;

public class G64Tests
{
    static byte[] SampleDisk()
    {
        var disk = D64Image.Create("TESTDISK", "AB");
        disk.Write("HELLO", FileType.Prg, Enumerable.Range(0, 700).Select(i => (byte)(i * 7)).ToArray(), false);
        disk.Write("DATA", FileType.Seq, System.Text.Encoding.ASCII.GetBytes("ONE\rTWO\r"), false);
        return disk.ToArray();
    }

    [Fact]
    public void AnEncodedDiskDecodesToTheSameSectors()
    {
        var d64 = SampleDisk();
        var g64 = G64Image.Encode(d64);
        Assert.True(G64Image.IsG64(g64));
        var (disk, report) = G64Image.Decode(g64);
        Assert.True(report.Clean, report.ToString());
        Assert.Equal(683, report.SectorsFound);
        Assert.Equal(d64, disk);
    }

    [Fact]
    public void TheDecodedDiskIsAUsableDrive()
    {
        var (disk, _) = G64Image.Decode(G64Image.Encode(SampleDisk()));
        var drive = new D64Image(disk);
        Assert.Equal("TESTDISK", drive.Title);
        Assert.Equal(new[] { "HELLO", "DATA" }, drive.Directory().Select(e => e.Name));
        Assert.Equal(700, drive.Read("HELLO").Data.Length);
    }

    [Fact]
    public void ABadDataChecksumIsReportedAndTheSectorIsMissing()
    {
        var g64 = G64Image.Encode(SampleDisk());
        // flip bits in the middle of track 1's data: whichever sector that is fails its checksum
        int track1 = BitConverter.ToInt32(g64, 12) + 2;
        g64[track1 + 150] ^= 0x18;
        var (_, report) = G64Image.Decode(g64);
        Assert.False(report.Clean);
        Assert.True(report.BadDataChecksums + report.BadHeaders >= 1);
        Assert.True(report.SectorsMissing >= 1);
    }

    [Fact]
    public void DataOnAHalfTrackIsFlagged()
    {
        var g64 = G64Image.Encode(SampleDisk());
        int track1 = BitConverter.ToInt32(g64, 12);
        BitConverter.GetBytes(track1).CopyTo(g64, 12 + 1 * 4);        // half-track 1.5 points at the same bytes
        var (_, report) = G64Image.Decode(g64);
        Assert.Equal(1, report.HalfTracksStored);
        Assert.False(report.Clean);
        Assert.Contains(report.Notes, n => n.Contains("half-track"));
    }

    [Fact]
    public void ATrackBeyond35IsNoted()
    {
        var g64 = G64Image.Encode(SampleDisk());
        int track1 = BitConverter.ToInt32(g64, 12);
        BitConverter.GetBytes(track1).CopyTo(g64, 12 + 70 * 4);       // slot 70 = track 36
        var (_, report) = G64Image.Decode(g64);
        Assert.Equal(1, report.ExtraTracks);
        Assert.Equal(683, report.SectorsFound);                       // the ordinary sectors are all still there
    }

    [Fact]
    public void ANonG64FileIsRefused()
    {
        Assert.False(G64Image.IsG64(new byte[1000]));
        Assert.Throws<InvalidDataException>(() => G64Image.Decode(new byte[1000]));
    }

    [Fact]
    public void TheEncodedTracksUseTheRealZoneLengths()
    {
        var g64 = G64Image.Encode(SampleDisk());
        int Length(int track) { int o = BitConverter.ToInt32(g64, 12 + (track - 1) * 8); return g64[o] | g64[o + 1] << 8; }
        Assert.Equal(7692, Length(1));
        Assert.Equal(7142, Length(20));
        Assert.Equal(6666, Length(27));
        Assert.Equal(6250, Length(35));
    }
}
