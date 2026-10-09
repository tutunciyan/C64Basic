using C64Basic.Core.Disk;
using C64Basic.Core.Rom;

namespace C64Basic.Tests;

/// <summary>What the window's hotkeys use: disk swapping within a folder, pausing, the register line.</summary>
public class GuiSupportTests
{
    static string Folder()
    {
        string dir = Path.Combine(Path.GetTempPath(), "c64swap-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void TheNextAndPreviousImageInAFolderWrapAround()
    {
        string dir = Folder();
        try
        {
            foreach (string name in new[] { "game side 1.d64", "game side 2.d64", "Game side 3.g64", "notes.txt", "x.prg" })
                File.WriteAllBytes(Path.Combine(dir, name), new byte[1]);
            string one = Path.Combine(dir, "game side 1.d64"), two = Path.Combine(dir, "game side 2.d64"), three = Path.Combine(dir, "Game side 3.g64");
            Assert.Equal(two, RomMachine.SiblingImage(one, 1));
            Assert.Equal(three, RomMachine.SiblingImage(two, 1));
            Assert.Equal(one, RomMachine.SiblingImage(three, 1));          // wraps
            Assert.Equal(three, RomMachine.SiblingImage(one, -1));
            Assert.Equal(one, RomMachine.SiblingImage(two, -1));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void ALoneImageHasNoSibling()
    {
        string dir = Folder();
        try
        {
            string only = Path.Combine(dir, "only.d64");
            File.WriteAllBytes(only, new byte[1]);
            Assert.Null(RomMachine.SiblingImage(only, 1));
            Assert.Null(RomMachine.SiblingImage(Path.Combine(dir, "missing.d64"), 1));
        }
        finally { Directory.Delete(dir, true); }
    }

    [RomFact]
    public void SwappingDisksMountsTheNeighbourAndShowsItsDirectory()
    {
        string dir = Folder();
        try
        {
            foreach (var (file, name) in new[] { ("a.d64", "FIRST SIDE"), ("b.d64", "SECOND SIDE") })
                File.WriteAllBytes(Path.Combine(dir, file), D64Image.Create(name, "01").ToArray());
            var m = new RomMachine(TestRoms.Find()!);
            m.RunSeconds(3.0);
            m.MountDiskFile(Path.Combine(dir, "a.d64"));
            Assert.Equal(Path.Combine(dir, "b.d64"), m.SwapDisk(1));
            Assert.Equal(Path.Combine(dir, "b.d64"), m.DiskPath);
            m.Type("LOAD\"$\",8\rLIST\r");
            Assert.True(m.RunUntil(() => m.ScreenText().Contains("SECOND SIDE"), 20), m.ScreenText());
            Assert.Equal(Path.Combine(dir, "a.d64"), m.SwapDisk(1));
        }
        finally { Directory.Delete(dir, true); }
    }

    [RomFact]
    public void APausedMachineStandsStillAndTellsItsRegisters()
    {
        var m = new RomMachine(TestRoms.Find()!);
        m.RunSeconds(1.0);
        string line = m.Registers();
        Assert.Matches(@"^PC=[0-9A-F]{4} A=[0-9A-F]{2} X=[0-9A-F]{2} Y=[0-9A-F]{2} SP=[0-9A-F]{2} [Nn][Vv]-[Bb][Dd][Ii][Zz][Cc] line \d+ \| 1541 #8 PC=[0-9A-F]{4} track 1$", line);
        m.Paused = true;
        long before = m.Cycles;
        var worker = new Thread(() => m.RunPaced(() => !m.Paused && m.Cycles > before + 100_000, () => false)) { IsBackground = true };
        worker.Start();
        Thread.Sleep(300);
        Assert.Equal(before, m.Cycles);                                  // paused: no time passes
        m.Paused = false;
        Assert.True(worker.Join(10_000));
        Assert.True(m.Cycles > before);
    }
}
