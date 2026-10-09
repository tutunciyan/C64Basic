using C64Basic.Core.Disk;
using C64Basic.Core.Rom;

namespace C64Basic.Tests;

/// <summary>A second 1541 (device 9) on the serial bus: its own 6502, DOS and disk, in the same lockstep as the C64 and drive 8.</summary>
public class SecondDriveTests
{
    static byte[] DiskWith(string name, string id, string file)
    {
        var d = D64Image.Create(name, id);
        d.Write(file, FileType.Prg, new byte[] { 1, 8, 0, 0, 0 }, replace: false);
        return d.ToArray();
    }

    static RomMachine TwoDrives()
    {
        var roms = TestRoms.Find()!;
        var m = new RomMachine(roms, driveCount: 2);
        m.RunSeconds(3.0);
        return m;
    }

    [RomFact]
    public void EachDriveListsItsOwnDisk()
    {
        var m = TwoDrives();
        m.MountDisk(DiskWith("DISK EIGHT", "E8", "ON EIGHT"), 8);
        m.MountDisk(DiskWith("DISK NINE", "N9", "ON NINE"), 9);
        m.Type("LOAD\"$\",8\rLIST\r");
        Assert.True(m.RunUntil(() => m.ScreenText().Contains("BLOCKS FREE"), 20), m.ScreenText());
        Assert.Contains("ON EIGHT", m.ScreenText());
        Assert.DoesNotContain("ON NINE", m.ScreenText());
        m.Type("\u0093");                                        // clear the screen
        m.RunSeconds(0.3);
        m.Type("LOAD\"$\",9\rLIST\r");
        Assert.True(m.RunUntil(() => m.ScreenText().Contains("ON NINE") && m.ScreenText().Contains("BLOCKS FREE"), 20), m.ScreenText());
        Assert.Null(m.HaltReason);
    }

    [RomFact]
    public void AProgramLoadedFromOneDriveCanBeSavedToTheOther()
    {
        string dir = Path.Combine(Path.GetTempPath(), "c64two-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var m = TwoDrives();
            m.MountDisk(DiskWith("SOURCE", "S1", "TOOL"), 8);
            string target = Path.Combine(dir, "target.d64");
            m.MountDiskFile(target, 9);
            Assert.Equal(target, m.DiskPathOf(9));
            Assert.Null(m.DiskPathOf(8));
            int writes = 0;
            m.DiskWritten += _ => writes++;
            m.Type("LOAD\"TOOL\",8\rSAVE\"COPY\",9\r");
            Assert.True(m.RunUntil(() => writes > 0, 40), m.ScreenText());
            m.RunSeconds(0.5);
            Assert.Null(m.SaveError);
            Assert.Contains(new D64Image(File.ReadAllBytes(target)).Directory(), e => e.Name == "COPY");
            Assert.DoesNotContain(new D64Image(m.Drives[0].Mechanics.Disk!.ToD64().Disk).Directory(), e => e.Name == "COPY");
        }
        finally { Directory.Delete(dir, true); }
    }

    [RomFact]
    public void ADeviceThatIsNotThereGivesDeviceNotPresentAndTheStateKeepsBothDrives()
    {
        var m = TwoDrives();
        m.MountDisk(DiskWith("NINE", "N9", "KEEP"), 9);
        m.Type("LOAD\"$\",10\r");
        Assert.True(m.RunUntil(() => m.ScreenText().Contains("DEVICE NOT PRESENT"), 20), m.ScreenText());
        byte[] state = m.SaveState();
        var other = new RomMachine(TestRoms.Find()!, driveCount: 2);
        other.LoadState(state);
        m.RunSeconds(2);
        other.RunSeconds(2);
        Assert.Equal(m.Cpu.Cycles, other.Cpu.Cycles);
        Assert.Equal(m.Drives[1].Cycles, other.Drives[1].Cycles);
        Assert.Equal(m.Drives[1].Cpu.PC, other.Drives[1].Cpu.PC);
        Assert.Throws<InvalidDataException>(() => new RomMachine(TestRoms.Find()!).LoadState(state));
        Assert.Throws<ArgumentOutOfRangeException>(() => m.MountDisk(DiskWith("X", "X1", "X"), 10));
    }
}
