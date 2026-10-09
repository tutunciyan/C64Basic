using C64Basic.Core.Machine;
using C64Basic.Core.Rom;

namespace C64Basic.Tests;

/// <summary>The serial bus wiring between CIA 2 and a drive's VIA 1, without any ROM.</summary>
public class IecBusTests
{
    static (Bus Bus, Drive1541 Drive, IecBus Iec) Setup(int device = 8)
    {
        Drive1541? drive = null;
        var bus = new Bus { Seconds = () => ((drive?.Cpu.AccessCycle ?? 0) + 0.5) / Drive1541.ClockHz };   // the drive's clock for both
        drive = new Drive1541(new byte[Drive1541.RomSize]);
        var iec = new IecBus(bus.Cia2);
        iec.Attach(drive, device);
        bus.Cia2.Write(0xDD02, 0x3F);
        bus.Cia2.Write(0xDD00, 0x00);
        return (bus, drive, iec);
    }

    static void DriveOutputs(Drive1541 d, byte ddrb, byte orb)
    {
        d.Via1.Write(2, ddrb);
        d.Via1.Write(0, orb);
    }

    [Fact]
    public void IdleLinesAreHighAndTheC64ReadsThemOnPa6AndPa7()
    {
        var (bus, drive, iec) = Setup();
        DriveOutputs(drive, 0x1A, 0x00);
        Assert.True(iec.Atn && iec.Clk && iec.Data);
        Assert.Equal(0xC0, bus.Cia2.Read(0xDD00) & 0xC0);
    }

    [Fact]
    public void TheC64PullsALineLowByWritingAOneToItsBit()
    {
        var (bus, drive, iec) = Setup();
        DriveOutputs(drive, 0x1A, 0x00);
        bus.Cia2.Write(0xDD00, 0x10);                       // CLK out
        Assert.False(iec.Clk);
        Assert.Equal(0x80, bus.Cia2.Read(0xDD00) & 0xC0);   // CLK in reads low, DATA still high
        bus.Cia2.Write(0xDD00, 0x20);                       // DATA out
        Assert.True(iec.Clk);
        Assert.False(iec.Data);
        Assert.Equal(0x40, bus.Cia2.Read(0xDD00) & 0xC0);
        bus.Cia2.Write(0xDD00, 0x08);                       // ATN
        Assert.False(iec.Atn);
    }

    [Fact]
    public void ALineStaysLowWhileAnyPartyHoldsIt()
    {
        var (bus, drive, iec) = Setup();
        DriveOutputs(drive, 0x1A, 0x08);                    // the drive holds CLK
        bus.Cia2.Write(0xDD00, 0x10);                       // and so does the C64
        Assert.False(iec.Clk);
        bus.Cia2.Write(0xDD00, 0x00);
        Assert.False(iec.Clk);                              // the drive still does
        drive.Via1.Write(0, 0x00);
        Assert.True(iec.Clk);
    }

    [Fact]
    public void TheDriveReadsTheLinesInvertedOnPb0Pb2AndPb7()
    {
        var (bus, drive, _) = Setup();
        DriveOutputs(drive, 0x1A, 0x00);
        Assert.Equal(0x00, drive.Via1.Read(0) & 0x85);
        bus.Cia2.Write(0xDD00, 0x30);                       // CLK and DATA low
        Assert.Equal(0x05, drive.Via1.Read(0) & 0x85);
        bus.Cia2.Write(0xDD00, 0x08);                       // ATN low (the hardware also pulls DATA low)
        Assert.Equal(0x81, drive.Via1.Read(0) & 0x85);
    }

    [Theory]
    [InlineData(8, 0x00)]
    [InlineData(9, 0x20)]
    [InlineData(10, 0x40)]
    [InlineData(11, 0x60)]
    public void TheDeviceNumberIsOnPb5AndPb6(int device, int bits)
    {
        var (_, drive, _) = Setup(device);
        DriveOutputs(drive, 0x1A, 0x00);
        Assert.Equal(bits, drive.Via1.Read(0) & 0x60);
    }

    [Fact]
    public void AssertingAtnPullsDataLowAtOnceUntilTheDriveAcknowledgesWithAtna()
    {
        var (bus, drive, iec) = Setup();
        DriveOutputs(drive, 0x1A, 0x00);
        Assert.True(iec.Data);
        bus.Cia2.Write(0xDD00, 0x08);                       // ATN asserted
        Assert.False(iec.Data);                             // the hardware answers by itself
        drive.Via1.Write(0, 0x10);                          // ATNA set: matches the asserted ATN, DATA is released
        Assert.True(iec.Data);
        bus.Cia2.Write(0xDD00, 0x00);                       // ATN released while ATNA is still set
        Assert.False(iec.Data);
        drive.Via1.Write(0, 0x00);
        Assert.True(iec.Data);
    }

    [Fact]
    public void TheDriveCanPullDataLowItself()
    {
        var (_, drive, iec) = Setup();
        DriveOutputs(drive, 0x1A, 0x02);
        Assert.False(iec.Data);
    }

    [Fact]
    public void AnAttentionEdgeIsAnInterruptOnCa1WhenPcrSelectsThePositiveEdge()
    {
        var (bus, drive, _) = Setup();
        DriveOutputs(drive, 0x1A, 0x00);
        drive.Via1.Write(12, 0x01);                         // PCR: CA1 on the positive edge, as the DOS sets it
        drive.Via1.Write(14, 0x82);
        Assert.False(drive.Via1.IrqActive);
        bus.Cia2.Write(0xDD00, 0x08);                       // ATN low: CA1 (the inverted line) rises
        Assert.True(drive.Via1.IrqActive);
        drive.Via1.Read(1);
        Assert.False(drive.Via1.IrqActive);
        bus.Cia2.Write(0xDD00, 0x00);                       // releasing ATN is the other edge: nothing
        Assert.False(drive.Via1.IrqActive);
    }

    [Fact]
    public void ADeviceOutsideEightToElevenIsRefused()
    {
        var bus = new Bus();
        var iec = new IecBus(bus.Cia2);
        var drive = new Drive1541(new byte[Drive1541.RomSize]);
        Assert.Throws<ArgumentOutOfRangeException>(() => iec.Attach(drive, 12));
    }
}
