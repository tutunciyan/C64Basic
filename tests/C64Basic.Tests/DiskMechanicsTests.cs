using C64Basic.Core.Disk;
using C64Basic.Core.Machine;
using C64Basic.Core.Rom;

namespace C64Basic.Tests;

/// <summary>The head, the spindle and the read channel of the 1541, and the real DOS reading real sectors through them.</summary>
public class DiskMechanicsTests
{
    static byte[] BlankD64(string name = "TEST DISK", string id = "T1")
    {
        var image = D64Image.Create(name, id);
        return image.ToArray();
    }

    /// <summary>A drive with a ROM of NOPs (it only needs to exist) and a disk in it, with VIA 2 set up as the DOS does.</summary>
    static Drive1541 BareDrive(GcrDisk? disk, bool motor = true)
    {
        var drive = new Drive1541(new byte[Drive1541.RomSize]);
        drive.Reset();
        drive.Via2.Write(2, 0x6F);
        drive.Via2.Write(0, (byte)(motor ? 0x04 : 0x00));
        drive.InsertDisk(disk, swap: false);
        return drive;
    }

    [Fact]
    public void ADiskFromAD64HasThirtyFiveTracksOfGcrAtTheRightSpeedZones()
    {
        var disk = GcrDisk.FromD64(BlankD64());
        for (int track = 1; track <= 35; track++) Assert.NotNull(disk.Tracks[(track - 1) * 2]);
        Assert.Null(disk.Tracks[1]);                    // no half-tracks
        Assert.Null(disk.Tracks[70]);                   // nothing beyond track 35
        Assert.Equal(3, disk.Speeds[0]);                // track 1
        Assert.Equal(3, disk.Speeds[16 * 2]);           // track 17
        Assert.Equal(2, disk.Speeds[17 * 2]);           // track 18
        Assert.Equal(1, disk.Speeds[24 * 2]);           // track 25
        Assert.Equal(0, disk.Speeds[30 * 2]);           // track 31
        Assert.Equal(7692, disk.TrackBytes(0));
        Assert.Equal(6250, disk.TrackBytes(34 * 2));
    }

    [Fact]
    public void AG64KeepsHalfTracksAndPerTrackSpeeds()
    {
        var g64 = G64Image.Encode(BlankD64());
        var disk = GcrDisk.FromImage(g64);
        Assert.NotNull(disk.Tracks[0]);
        Assert.Equal(GcrDisk.FromD64(BlankD64()).Tracks[0], disk.Tracks[0]);
    }

    [Fact]
    public void TheStepperMovesTheHeadAHalfTrackPerPhase()
    {
        var drive = BareDrive(GcrDisk.FromD64(BlankD64()));
        var m = drive.Mechanics;
        Assert.Equal(0, m.HalfTrack);
        foreach (int phase in new[] { 1, 2, 3, 0, 1 }) drive.Via2.Write(0, (byte)(0x04 | phase));
        Assert.Equal(5, m.HalfTrack);
        Assert.Equal(3.5, m.Track);
        foreach (int phase in new[] { 0, 3, 2 }) drive.Via2.Write(0, (byte)(0x04 | phase));
        Assert.Equal(2, m.HalfTrack);
        foreach (int phase in new[] { 1, 0, 3, 2, 1, 0 }) drive.Via2.Write(0, (byte)(0x04 | phase));
        Assert.Equal(0, m.HalfTrack);                   // the stop at the edge
        drive.Via2.Write(0, 0x04 | 3);
        Assert.Equal(0, m.HalfTrack);
    }

    [Fact]
    public void TheHeadCannotGoPastTheInnerEdgeEither()
    {
        var drive = BareDrive(null);
        for (int i = 0; i < 120; i++) drive.Via2.Write(0, (byte)(0x04 | (i + 1) & 3));
        Assert.Equal(GcrDisk.Slots - 1, drive.Mechanics.HalfTrack);
    }

    [Fact]
    public void WithTheMotorOffNothingTurns()
    {
        var drive = BareDrive(GcrDisk.FromD64(BlankD64()), motor: false);
        drive.Mechanics.AdvanceTo(1_000_000);
        Assert.Equal(0, drive.Mechanics.BytesRead);
        Assert.Equal(0, drive.Mechanics.Revolutions);
    }

    [Fact]
    public void ATrackInTheOuterZoneTakesTwoHundredMillisecondsAndDeliversAByteEvery26Cycles()
    {
        var drive = BareDrive(GcrDisk.FromD64(BlankD64()));
        drive.Mechanics.AdvanceTo(1_000_000);
        Assert.Equal(5, drive.Mechanics.Revolutions);
        Assert.InRange(drive.Mechanics.BytesRead, 37_000, 38_000);      // 1,000,000 / 26 bytes, less the sync marks
    }

    [Fact]
    public void TheInnerZoneIsSlower()
    {
        var disk = GcrDisk.FromD64(BlankD64());
        var drive = BareDrive(disk);
        for (int i = 0; i < 60; i++) drive.Via2.Write(0, (byte)(0x04 | (i + 1) & 3));     // 30 tracks in: track 31
        Assert.Equal(60, drive.Mechanics.HalfTrack);
        long start = drive.Mechanics.Revolutions;
        drive.Mechanics.AdvanceTo(1_000_000);
        Assert.Equal(5, drive.Mechanics.Revolutions - start);           // still 300 rpm: 6250 bytes at 32 cycles
    }

    [Fact]
    public void ThereIsNoSyncAndNoDataWithoutADisk()
    {
        var drive = BareDrive(null);
        drive.Mechanics.AdvanceTo(500_000);
        Assert.False(drive.Mechanics.Sync);
        Assert.Equal(0, drive.Mechanics.BytesRead);
        Assert.Equal(0x80, drive.Via2.Read(0) & 0x80);                  // PB7 high: no sync
    }

    [Fact]
    public void ThePortsShowSyncWriteProtectAndTheLastByte()
    {
        var disk = GcrDisk.FromD64(BlankD64());
        var drive = BareDrive(disk);
        Assert.NotEqual(0, drive.Via2.Read(0) & 0x10);                  // not write protected
        disk.WriteProtected = true;
        Assert.Equal(0, drive.Via2.Read(0) & 0x10);
        drive.Mechanics.AdvanceTo(50);                                  // the track starts with a sync mark (40 bits)
        Assert.Equal(0, drive.Via2.Read(0) & 0x80);
    }

    /// <summary>Firmware: motor on, wait for a sync mark, then store the next eight bytes the head reads at $0300.</summary>
    static readonly string ReadEightBytes =
        "A9 6F 8D 02 1C A9 04 8D 00 1C A9 0E 8D 0C 1C " +            // DDRB=$6F, motor on, PCR: CA2 high (SO enabled)
        "2C 00 1C 30 FB " +                                           // BIT $1C00 : BMI back   (until SYNC)
        "B8 A2 00 " +                                                 // CLV : LDX #0
        "50 FE B8 AD 01 1C 9D 00 03 E8 E0 08 D0 F2 " +                // BVC * : CLV : LDA $1C01 : STA $0300,X : INX : CPX #8 : BNE
        "4C 25 C0";

    [Fact]
    public void TheProcessorSeesTheBytesAfterASyncMarkThroughByteReadyAndTheVFlag()
    {
        var rom = new byte[Drive1541.RomSize];
        Array.Fill(rom, (byte)0xEA);
        Convert.FromHexString(ReadEightBytes.Replace(" ", "")).CopyTo(rom, 0);
        rom[0x3FFC] = 0x00; rom[0x3FFD] = 0xC0;
        var drive = new Drive1541(rom);
        var disk = GcrDisk.FromD64(BlankD64());
        drive.InsertDisk(disk);
        drive.Reset();
        long end = 20_000;
        while (drive.Cycles < end) drive.Step();

        var got = drive.Ram.AsSpan(0x300, 8).ToArray();
        Assert.Equal(0x52, got[0]);                                     // the header block's marker byte
        var track = disk.Tracks[0]!;
        int at = track.AsSpan().IndexOf(got);
        Assert.True(at >= 5, "the bytes read are the ones on the disk, right after a sync mark");
        Assert.Equal(0xFF, track[at - 1]);
        Assert.Equal(0xC025, drive.Cpu.PC);                             // the program ran to its end loop
    }

    // ---------- the real DOS ----------
    static byte[] SomeBytes(int count)
    {
        var data = new byte[count];
        uint x = 12345;
        for (int i = 0; i < count; i++) { x = x * 1664525 + 1013904223; data[i] = (byte)(x >> 24); }
        return data;
    }

    [Fact]
    public void TheDosReadsTheDirectoryOfADiskThroughTheHead()
    {
        var roms = TestRoms.Find();
        if (roms == null) return;
        var image = D64Image.Create("SPIN TEST", "ST");
        image.Write("HELLO", FileType.Prg, new byte[] { 1, 8, 1, 2, 3 }, replace: false);
        var host = new IecHost(roms.Dos);
        host.Drive.InsertDisk(GcrDisk.FromD64(image.ToArray()));
        host.Run(1_500_000);

        var listing = host.Load("$");
        Assert.NotNull(listing);
        string text = new string(listing!.Select(b => b >= 32 && b < 127 ? (char)b : '.').ToArray());
        Assert.Contains("SPIN TEST", text);
        Assert.Contains("HELLO", text);
        Assert.True(host.Drive.Mechanics.BytesRead > 1000);
        Assert.Equal(1, host.Drive.Mechanics.Track > 17 ? 1 : 0);       // the head went to the directory track
    }

    [Fact]
    public void TheDosReadsAFileThatSpansManySectors()
    {
        var roms = TestRoms.Find();
        if (roms == null) return;
        var payload = SomeBytes(3000);
        var program = new byte[payload.Length + 2];
        program[0] = 0x00; program[1] = 0x40;
        payload.CopyTo(program, 2);
        var image = D64Image.Create("BIG", "01");
        image.Write("DATA", FileType.Prg, program, replace: false);
        var host = new IecHost(roms.Dos);
        host.Drive.InsertDisk(GcrDisk.FromD64(image.ToArray()));
        host.Run(1_500_000);

        var loaded = host.Load("DATA");
        Assert.NotNull(loaded);
        Assert.Equal(program, loaded);
    }

    [Fact]
    public void TheDosReadsTheDirectoryOfALocalG64Image()
    {
        // a game image in samples/ (copyrighted, git-ignored): its raw tracks go through the same head and read channel
        var roms = TestRoms.Find();
        if (roms == null) return;
        string? path = null;
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null && path == null; dir = dir.Parent)
        {
            string samples = Path.Combine(dir.FullName, "samples");
            if (Directory.Exists(samples)) path = Directory.GetFiles(samples, "*.g64").FirstOrDefault();
        }
        if (path == null) return;
        var host = new IecHost(roms.Dos);
        host.Drive.InsertDisk(GcrDisk.FromImage(File.ReadAllBytes(path)));
        host.Run(1_500_000);
        var listing = host.Load("$");
        Assert.NotNull(listing);
        Assert.True(listing!.Length > 40, "a directory with a header, at least one file and a blocks-free line");
        Assert.Equal(0x01, listing[0]);
        Assert.Equal(0x04, listing[1]);
    }

    [Fact]
    public void WithoutADiskTheDriveReportsNotReady()
    {
        var roms = TestRoms.Find();
        if (roms == null) return;
        var host = new IecHost(roms.Dos);
        host.Run(1_500_000);
        Assert.NotNull(host.Load("$"));                                 // it answers, with nothing
        Assert.True(host.Command(0x48, 0x6F));
        Assert.True(host.TurnAround());
        Assert.StartsWith("74,DRIVE NOT READY", host.ReceiveLine());
    }
}
