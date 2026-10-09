using C64Basic.Core.Machine;
using C64Basic.Core.Rom;
using Xunit;

namespace C64Basic.Tests;

/// <summary>
/// Real game images in <c>roms/games</c> (copyrighted and git-ignored, so these tests do nothing on a machine without them): each
/// is mounted in the ROM mode machine, loaded and started the way a user would, and has to get to a picture without a halt.
/// A game that works on a real C64 and fails here is a bug in the machine; add its image and a row here.
/// </summary>
public class GameCorpusTests
{
    static IEnumerable<string> Images()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            string games = Path.Combine(dir.FullName, "roms", "games");
            if (!Directory.Exists(games)) continue;
            return Directory.GetFiles(games).Where(f => Path.GetExtension(f).ToLowerInvariant() is ".d64" or ".g64" or ".t64" or ".prg");
        }
        return Enumerable.Empty<string>();
    }

    static int Colours(RomMachine m)
    {
        var frame = new uint[Vic2.FrameWidth * Vic2.FrameHeight];
        m.Bus.Vic.Render(frame);
        return frame.Distinct().Count();
    }

    [RomFact]
    public void EveryImageShowsItsDirectory()
    {
        var roms = TestRoms.Find();
        if (roms == null) return;
        foreach (string path in Images())
        {
            var m = new RomMachine(roms);
            m.RunSeconds(3.0);
            m.MountDiskFile(path);
            m.Type("LOAD\"$\",8\rLIST\r");
            Assert.True(m.RunUntil(() => m.ScreenText().Contains("BLOCKS FREE"), 30), Path.GetFileName(path) + "\n" + m.ScreenText());
            Assert.Null(m.HaltReason);
        }
    }

    [GameFact("International*.d64")]
    public void InternationalKaratePlusLoadsAndGetsPastItsIntro()
    {
        var roms = TestRoms.Find();
        string? path = TestRoms.FindGame("International*.d64");
        if (roms == null || path == null) return;
        var m = new RomMachine(roms);
        m.RunSeconds(3.0);
        m.MountDiskFile(path);
        m.Type("LOAD\"IK+*\",8,1\rRUN\r");               // a KERNAL load of 200 blocks (about two minutes), then the cracker's intro
        Assert.True(m.RunUntil(() => m.Cpu.PC < 0xA000 && m.Bus.Ram[0xC6] == 0 && Colours(m) >= 10, 200), "the intro is on the screen\n" + m.ScreenText());
        for (int i = 0; i < 4 && !m.ScreenText().Contains("4-Player", StringComparison.OrdinalIgnoreCase); i++)
        {
            m.Input.SetKey(60, true);                       // SPACE
            m.RunSeconds(0.4);
            m.Input.SetKey(60, false);
            m.RunSeconds(15);
        }
        Assert.Null(m.HaltReason);
        Assert.True(Colours(m) >= 2);
    }

    [GameFact("MSPACMAN.T64")]
    public void MsPacManLoadsFromATapeImageAndShowsItsTitle()
    {
        var roms = TestRoms.Find();
        string? path = TestRoms.FindGame("MSPACMAN.T64");
        if (roms == null || path == null) return;
        var m = new RomMachine(roms);
        m.RunSeconds(3.0);
        m.MountDiskFile(path);                              // the programs of the tape go on a blank disk
        m.Type("LOAD\"MSPACMAN\",8,1\rRUN\r");
        Assert.True(m.RunUntil(() => m.Cpu.PC < 0xA000 && Colours(m) >= 5, 90), m.ScreenText());
        m.RunSeconds(5);
        Assert.Null(m.HaltReason);
        Assert.True(Colours(m) >= 5);
    }
}
