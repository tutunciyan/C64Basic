using C64Basic.Core.Disk;
using C64Basic.Core.Rom;
using Xunit;

namespace C64Basic.Tests;

/// <summary>The density bits, speeds written to the disk, the stepper's jumps and writing a G64 back.</summary>
public class DriveAccuracyTests
{
    static byte[] BlankD64() => D64Image.Create("ACCURACY", "AC").ToArray();

    // motor on at a density, wait for SYNC, store the eight bytes after it at $0300
    static string ReadEight(int density) =>
        "A9 6F 8D 02 1C A9 " + (0x04 | density << 5).ToString("X2") + " 8D 00 1C A9 0E 8D 0C 1C " +
        "2C 00 1C 30 FB B8 A2 00 50 FE B8 AD 01 1C 9D 00 03 E8 E0 08 D0 F2 4C 25 C0";

    static Drive1541 Firmware(string code, GcrDisk disk)
    {
        var rom = new byte[Drive1541.RomSize];
        Array.Fill(rom, (byte)0xEA);
        Convert.FromHexString(code.Replace(" ", "")).CopyTo(rom, 0);
        rom[0x3FFC] = 0x00; rom[0x3FFD] = 0xC0;
        var drive = new Drive1541(rom);
        drive.InsertDisk(disk, swap: false);
        drive.Reset();
        return drive;
    }

    // ---------- the density bits ----------
    [Fact]
    public void ATrackIsReadCleanlyOnlyAtTheDensityItWasRecordedAt()
    {
        foreach (int density in new[] { 0, 1, 2, 3 })
        {
            var disk = GcrDisk.FromD64(BlankD64());               // track 1 is in the fastest zone, 3
            var drive = Firmware(ReadEight(density), disk);
            while (drive.Cycles < 30_000) drive.Step();
            var got = drive.Ram.AsSpan(0x300, 8).ToArray();
            bool onTheDisk = disk.Tracks[0]!.AsSpan().IndexOf(got) >= 0;
            Assert.Equal(density == 3, onTheDisk);
        }
    }

    [Fact]
    public void ACellIsCountedAtTheDrivesDensityNotTheTracks()
    {
        // at density 3 a bit takes 3.25 cycles; at density 0 it takes 4, so the same time delivers fewer bytes
        var fast = Bare(0x64);
        fast.Mechanics.AdvanceTo(400_000);
        var slow = Bare(0x04);
        slow.Mechanics.AdvanceTo(400_000);
        Assert.InRange(fast.Mechanics.BytesRead, 15_000, 16_000);          // 400,000 / 26 cycles a byte
        Assert.InRange(slow.Mechanics.BytesRead, 12_000, 12_600);          // 400,000 / 32
        // the disk turns at 300 rpm whatever the density: the slower cells just sample the same flux less often
        Assert.Equal(fast.Mechanics.Revolutions, slow.Mechanics.Revolutions);
    }

    [Fact]
    public void ReadingAtTheRightDensityAfterAWrongOneRecovers()
    {
        // the head's position inside a bit does not stay off: back at the matching density every bit is read as recorded
        var drive = Bare(0x04);                                  // density 0
        drive.Mechanics.AdvanceTo(50_000);
        drive.Via2.Write(0, 0x64);                               // density 3 again
        long read = drive.Mechanics.BytesRead;
        drive.Mechanics.AdvanceTo(250_000);
        Assert.InRange(drive.Mechanics.BytesRead - read, 7_000, 8_000);
    }

    // ---------- speeds on the disk ----------
    [Fact]
    public void WritingAtAnotherDensityRecordsThoseBytesAtThatSpeed()
    {
        // sixteen bytes written at density 2 on a track recorded at 3: they are slower, the rest of the track is not
        var disk = GcrDisk.FromD64(BlankD64());
        var drive = Firmware(
            "A9 6F 8D 02 1C A9 44 8D 00 1C A9 FF 8D 03 1C A9 A7 8D 01 1C A9 CE 8D 0C 1C A2 00 " +
            "50 FE B8 E8 E0 10 D0 F8 A9 EE 8D 0C 1C 4C 28 C0", disk);
        while (drive.Cycles < 40_000) drive.Step();
        Assert.True(disk.Modified);
        Assert.Equal(-1, disk.Speeds[0]);                       // per-byte speeds now
        var track = disk.Tracks[0]!;
        int at = track.AsSpan().IndexOf(Enumerable.Repeat((byte)0xA7, 15).ToArray());
        Assert.True(at >= 0);
        Assert.Equal(2, disk.SpeedAt(0, at + 4));
        Assert.Equal(3, disk.SpeedAt(0, 0));
        Assert.Equal(3, disk.SpeedAt(0, track.Length - 1));
    }

    [Fact]
    public void ATrackWithSpeedsThatChangeComesBackFromAG64WithThem()
    {
        var disk = GcrDisk.FromD64(BlankD64());
        disk.SetSpeed(0, 100, 1);
        disk.SetSpeed(0, 101, 2);
        disk.SetSpeed(0, 7000, 0);
        Assert.Equal(-1, disk.Speeds[0]);
        Assert.Equal(3, disk.SpeedAt(0, 99));
        var again = GcrDisk.FromG64(disk.ToG64());
        Assert.Equal(-1, again.Speeds[0]);
        for (int i = 0; i < disk.TrackBytes(0); i++) Assert.Equal(disk.SpeedAt(0, i), again.SpeedAt(0, i));
        Assert.Equal(1, again.SpeedAt(0, 100));
        Assert.Equal(0, again.SpeedAt(0, 7000));
        Assert.Equal(disk.Speeds.Skip(1), again.Speeds.Skip(1));
    }

    [Fact]
    public void SettingTheSpeedItAlreadyHasChangesNothing()
    {
        var disk = GcrDisk.FromD64(BlankD64());
        disk.SetSpeed(0, 10, 3);
        Assert.Equal(3, disk.Speeds[0]);
        Assert.Null(disk.SpeedTables[0]);
    }

    // ---------- the stepper ----------
    static Drive1541 Bare(int portB = 0x64)
    {
        var drive = new Drive1541(new byte[Drive1541.RomSize]);
        drive.Reset();
        drive.Via2.Write(2, 0x6F);
        drive.Via2.Write(0, (byte)portB);
        drive.InsertDisk(GcrDisk.FromD64(BlankD64()), swap: false);
        return drive;
    }

    static void Phases(Drive1541 drive, params int[] phases)
    {
        foreach (int phase in phases) drive.Via2.Write(0, (byte)(0x64 | phase));
    }

    [Fact]
    public void AJumpOfTwoPhasesGoesOnTheWayTheHeadWasGoing()
    {
        var drive = Bare();
        Phases(drive, 1, 2, 3, 0);
        Assert.Equal(4, drive.Mechanics.HalfTrack);
        Phases(drive, 2);                                        // the opposite coil: on, towards the centre
        Assert.Equal(6, drive.Mechanics.HalfTrack);
        Phases(drive, 1);                                        // a step back (6 -> 5)
        Assert.Equal(5, drive.Mechanics.HalfTrack);
        Phases(drive, 3);                                        // a jump of two, now going outwards
        Assert.Equal(3, drive.Mechanics.HalfTrack);
    }

    [Fact]
    public void AJumpAtTheEdgeStaysAtTheEdge()
    {
        var drive = Bare();
        Phases(drive, 2);                                        // from slot 0 (going in, by default): to slot 2
        Assert.Equal(2, drive.Mechanics.HalfTrack);
        Phases(drive, 1, 0);                                     // back to 0
        Assert.Equal(0, drive.Mechanics.HalfTrack);
        Phases(drive, 2);                                        // outwards past the stop: stays
        Assert.Equal(0, drive.Mechanics.HalfTrack);
    }

    // ---------- the state ----------
    [Fact]
    public void TheHeadsPositionInsideABitSurvivesASavedState()
    {
        var drive = Bare(0x04);                                  // density 0 on a zone 3 track: the head is between bits
        drive.Mechanics.AdvanceTo(30_001);
        var stream = new MemoryStream();
        using (var w = new BinaryWriter(stream, System.Text.Encoding.UTF8, true)) drive.Mechanics.SaveState(w);
        var other = Bare(0x04);
        stream.Position = 0;
        using (var r = new BinaryReader(stream)) other.Mechanics.LoadState(r);
        drive.Mechanics.AdvanceTo(60_000);
        other.Mechanics.AdvanceTo(60_000);
        Assert.Equal(drive.Mechanics.BytesRead, other.Mechanics.BytesRead);
        Assert.Equal(drive.Mechanics.Data, other.Mechanics.Data);
        Assert.Equal(drive.Mechanics.Revolutions, other.Mechanics.Revolutions);
    }

    // ---------- writing a G64 back (needs the ROMs: it is the real DOS that saves) ----------
    [RomFact]
    public void AG64IsWrittenBackOnlyWhenAskedAndTheOriginalIsKept()
    {
        var roms = TestRoms.Find();
        if (roms == null) return;
        string dir = Path.Combine(Path.GetTempPath(), "c64g64-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        bool before = RomMachine.SaveG64Changes;
        try
        {
            string path = Path.Combine(dir, "raw.g64");
            byte[] original = G64Image.Encode(D64Image.Create("RAW", "RW").ToArray());
            File.WriteAllBytes(path, original);
            RomMachine.SaveG64Changes = true;
            var m = new RomMachine(roms);
            m.RunSeconds(3.0);
            m.MountDiskFile(path);
            Assert.True(m.DiskSavesChanges);
            int writes = 0;
            m.DiskWritten += _ => writes++;
            m.Type("SAVE\"KEPT\",8\r");
            Assert.True(m.RunUntil(() => writes > 0, 30), m.ScreenText());
            m.RunSeconds(0.5);
            Assert.Null(m.SaveError);
            Assert.NotEqual(original, File.ReadAllBytes(path));
            Assert.Equal(original, File.ReadAllBytes(path + ".bak"));
            var (decoded, _) = G64Image.Decode(File.ReadAllBytes(path));
            Assert.Contains(new D64Image(decoded).Directory(), e => e.Name == "KEPT");
            Assert.False(File.Exists(path + ".tmp"));
        }
        finally
        {
            RomMachine.SaveG64Changes = before;
            Directory.Delete(dir, true);
        }
    }
}
