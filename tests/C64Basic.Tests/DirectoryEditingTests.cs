using C64Basic.Core.Disk;
using C64Basic.Core.Runtime;

namespace C64Basic.Tests;

public class DirectoryEditingTests
{
    static (Interpreter Interp, TestConsole Console) Loaded()
    {
        var console = new TestConsole();
        var interp = new Interpreter(console, new MemoryFileSystem());
        var disk = D64Image.Create("TEST", "01");
        interp.MountDrive(8, disk);
        interp.ProcessLine("10 REM");
        foreach (var n in new[] { "A", "B", "C" }) interp.ProcessLine($"SAVE \"{n}\",8");
        interp.ProcessLine("LOAD \"$\",8");
        return (interp, console);
    }

    [Fact]
    public void FilesWithTheSameBlockCountAllShow()
    {
        var (interp, _) = Loaded();
        var lines = interp.Listing().ToList();
        Assert.Equal(5, lines.Count);
        Assert.Equal(new[] { "A", "B", "C" }, lines.Skip(1).Take(3).Select(l => l.Split('"')[1]));
    }

    [Fact]
    public void TypingALineReplacesTheFirstOneWithThatNumber()
    {
        var (interp, _) = Loaded();
        interp.ProcessLine("1 REM CHANGED");
        var lines = interp.Listing().ToList();
        Assert.Equal(5, lines.Count);
        Assert.Equal("1 REM CHANGED", lines[1]);
        Assert.Contains("\"B\"", lines[2]);        // the others are untouched
    }

    [Fact]
    public void ALowerNumberIsInsertedAtTheTop()
    {
        var (interp, _) = Loaded();
        interp.ProcessLine("0 REM");                // the same number as the header: replaces it
        Assert.Equal("0 REM", interp.Listing().First());
        Assert.Equal(5, interp.Listing().Count());
    }

    [Fact]
    public void ANewNumberGoesBeforeTheFirstLargerOne()
    {
        var (interp, _) = Loaded();
        interp.ProcessLine("2 REM NEW");
        var lines = interp.Listing().ToList();
        Assert.Equal("2 REM NEW", lines[4]);        // before 661 BLOCKS FREE
        Assert.StartsWith("661 ", lines[5]);
    }

    [Fact]
    public void DeletingALineRemovesJustTheFirst()
    {
        var (interp, _) = Loaded();
        interp.ProcessLine("1");
        var lines = interp.Listing().ToList();
        Assert.Equal(4, lines.Count);
        Assert.Contains("\"B\"", lines[1]);
    }

    [Fact]
    public void ANormalLoadWorksAgainAfterwards()
    {
        var (interp, _) = Loaded();
        interp.ProcessLine("LOAD \"A\",8");
        interp.ProcessLine("20 END");
        Assert.Equal(new[] { "10 REM", "20 END" }, interp.Listing());
    }

    [Fact]
    public void ADirectoryCanBeSavedBackAsAProgram()
    {
        var (interp, console) = Loaded();
        interp.ProcessLine("SAVE \"LISTING\",8");
        interp.ProcessLine("NEW");
        interp.ProcessLine("LOAD \"LISTING\",8");
        Assert.Equal(5, interp.Listing().Count());
        Assert.DoesNotContain("ERROR", console.Output);
    }
}
