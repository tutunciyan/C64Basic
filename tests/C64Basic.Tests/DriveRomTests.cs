using C64Basic.Core.Machine;
using C64Basic.Core.Rom;

namespace C64Basic.Tests;

/// <summary>The real 1541 DOS ROM running on the drive's processor, talked to over the serial bus. Needs the ROM dumps in <c>roms/</c>.</summary>
public class DriveRomTests
{
    [RomFact]
    public void TheDosBootsAndSetsUpItsChips()
    {
        var roms = TestRoms.Find();
        if (roms == null) return;
        var host = new IecHost(roms.Dos);
        host.Run(1_500_000);
        Assert.Equal(CpuStop.None, host.Drive.Cpu.StopReason);
        Assert.Equal(0x1A, host.Drive.Via1.Ddrb);          // DATA out, CLK out and ATNA are outputs
        Assert.Equal(0x02, host.Drive.Via1.Ier);           // the ATN edge interrupts
        Assert.Equal(0x40, host.Drive.Via2.Ier);           // the 10 ms job-queue timer
        Assert.True(host.ClkIsHigh && host.DataIsHigh);    // the drive leaves the bus alone when idle
    }

    [RomFact]
    public void ADriveAnswersAttentionAndReportsItsStatusOnTheCommandChannel()
    {
        var roms = TestRoms.Find();
        if (roms == null) return;
        var host = new IecHost(roms.Dos);
        host.Run(1_500_000);

        Assert.True(host.Command(0x28, 0x6F), "LISTEN 8, secondary 15 (the command channel)");
        host.Release();
        Assert.True(host.Command(0x48, 0x6F), "TALK 8, secondary 15");
        Assert.True(host.TurnAround());
        Assert.Equal("73,CBM DOS V2.6 1541,00,00\r", host.ReceiveLine());
        host.Release();
    }

    [RomFact]
    public void ADriveSetToDevice9IgnoresDevice8()
    {
        var roms = TestRoms.Find();
        if (roms == null) return;
        var host = new IecHost(roms.Dos, device: 9);
        host.Run(1_500_000);
        Assert.False(host.Command(0x28, 0x6F), "LISTEN 8 must go unanswered");
        host.Release();
        Assert.True(host.Command(0x29, 0x6F), "LISTEN 9");
        host.Release();
    }

    [RomFact]
    public void AnUnknownCommandSetsTheErrorChannel()
    {
        var roms = TestRoms.Find();
        if (roms == null) return;
        var host = new IecHost(roms.Dos);
        host.Run(1_500_000);

        Assert.True(host.Command(0x28, 0x6F));
        host.EndCommandAsTalker();
        Assert.True(host.Send((byte)'Q'));
        Assert.True(host.Send(13, eoi: true));
        host.Command(0x3F);                                // UNLISTEN
        host.Release();
        Assert.True(host.Command(0x48, 0x6F));
        Assert.True(host.TurnAround());
        Assert.StartsWith("31,SYNTAX ERROR,00,00", host.ReceiveLine());
    }
}
