using C64Basic.Core.Machine;

namespace C64Basic.Tests;

public class BusTests
{
    [Fact]
    public void PokeBorderReachesVic()
    {
        var bus = new Bus();
        bus.Write(53280, 3);
        Assert.Equal(3, bus.Vic.Border);
    }

    [Fact]
    public void VicColourRegistersReadWithUpperBitsSet()
    {
        var bus = new Bus();
        bus.Write(53280, 3);
        Assert.Equal(243, bus.Read(53280));
    }

    [Fact]
    public void VicRegistersMirrorEvery64Bytes()
    {
        var bus = new Bus();
        bus.Write(53280 + 64, 5);
        bus.Write(53281 + 128, 7);
        Assert.Equal(5, bus.Vic.Border);
        Assert.Equal(7, bus.Vic.Background);
    }

    [Fact]
    public void DefaultColoursAreLightBlueOnBlue()
    {
        var bus = new Bus();
        Assert.Equal(14, bus.Vic.Border);
        Assert.Equal(6, bus.Vic.Background);
        Assert.Equal(14, bus.Ram[646]);
    }

    [Fact]
    public void ColorRamKeepsFourBits()
    {
        var bus = new Bus();
        bus.Write(55296, 37);
        Assert.Equal(5, bus.Read(55296));
    }

    [Fact]
    public void UnmappedIoActsAsStorage()
    {
        var bus = new Bus();
        bus.Write(56832, 7);
        Assert.Equal(7, bus.Read(56832));
        Assert.Equal(129, bus.Read(56334));
    }

    [Fact]
    public void WritesWithIoHiddenGoToRamNotChips()
    {
        var bus = new Bus();
        bus.Write(1, 52);
        bus.Write(53280, 9);
        Assert.Equal(14, bus.Vic.Border);
        Assert.Equal(9, bus.Ram[53280]);
        Assert.Equal(9, bus.Read(53280));
    }

    [Fact]
    public void CharRomHidesIoForReadsButWritesGoToRam()
    {
        var bus = new Bus();
        bus.Write(1, 51);
        bus.Write(53280, 9);
        Assert.Equal(14, bus.Vic.Border);
        Assert.Equal(9, bus.Ram[53280]);
        Assert.Equal(bus.Read(53248 + 8), C64Basic.Core.Runtime.CharRom.Read(53248 + 8));
    }

    [Fact]
    public void WrittenEventReportsIoFlag()
    {
        var bus = new Bus();
        var seen = new List<(int Address, byte Value, bool Io)>();
        bus.Written += (a, v, io) => seen.Add((a, v, io));
        bus.Write(1024, 1);
        bus.Write(53280, 2);
        bus.Ram[1] = 0x34;
        bus.Write(53280, 3);
        Assert.Equal(new[] { (1024, (byte)1, false), (53280, (byte)2, true), (53280, (byte)3, false) }, seen);
    }

    [Fact]
    public void MappedChipIsCalledWithAbsoluteAddress()
    {
        var bus = new Bus();
        var chip = new Recorder();
        bus.Map(0xD400, 0x20, chip);
        bus.Write(0xD404, 0x21);
        Assert.Equal(0x21, bus.Read(0xD404));
        Assert.Equal(0xD404, chip.LastAddress);
    }

    sealed class Recorder : IMemoryMapped
    {
        public int LastAddress;
        byte _value;
        public byte Read(int address) { LastAddress = address; return _value; }
        public void Write(int address, byte value) { LastAddress = address; _value = value; }
    }
}
