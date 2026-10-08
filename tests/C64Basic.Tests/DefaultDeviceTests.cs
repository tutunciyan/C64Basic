using C64Basic.Core.Disk;
using C64Basic.Core.Runtime;

namespace C64Basic.Tests;

public class DefaultDeviceTests
{
    [Fact]
    public void StrictModeLoadsAndSavesOnTapeLikeARealC64()
    {
        var console = new TestConsole();
        var interp = new Interpreter(console, new MemoryFileSystem(), new InterpreterOptions { Strict = true });
        interp.ProcessLine("10 REM");
        interp.ProcessLine("SAVE \"T\"");
        interp.ProcessLine("NEW");
        interp.ProcessLine("LOAD \"T\"");
        Assert.Equal("PRESS RECORD & PLAY ON TAPE\nOK\nSAVING T\nPRESS PLAY ON TAPE\nOK\nSEARCHING FOR T\nFOUND T\nLOADING\n", console.Output);
        Assert.Single(interp.Listing());
    }

    [Fact]
    public void StrictModeStillReachesTheDiskWithDevice8()
    {
        var console = new TestConsole();
        var interp = new Interpreter(console, new MemoryFileSystem(), new InterpreterOptions { Strict = true });
        var disk = D64Image.Create("D", "00");
        interp.MountDrive(8, disk);
        interp.ProcessLine("10 REM");
        interp.ProcessLine("SAVE \"D\",8");
        Assert.Equal("SAVING D\n", console.Output);
        Assert.Single(disk.Directory());
    }

    [Fact]
    public void ExtendedModeKeepsTheDiskAsTheDefault()
    {
        var console = new TestConsole();
        var interp = new Interpreter(console, new MemoryFileSystem());
        var disk = D64Image.Create("D", "00");
        interp.MountDrive(8, disk);
        interp.ProcessLine("10 REM");
        interp.ProcessLine("SAVE \"D\"");
        Assert.Equal("SAVING D\n", console.Output);
        Assert.Single(disk.Directory());
    }
}
