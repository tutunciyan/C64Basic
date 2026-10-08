using C64Basic.Core.Machine;

namespace C64Basic.Tests;

public class CycleTests
{
    static byte[] Hex(string hex) => Convert.FromHexString(hex.Replace(" ", ""));

    static (Cpu6502 Cpu, Bus Bus) Machine(string code, bool cycleAccurate = true)
    {
        var bus = new Bus();
        var cpu = new Cpu6502(bus) { CycleAccurate = cycleAccurate };
        Array.Copy(Hex(code), 0, bus.Ram, 0x200, Hex(code).Length);
        return (cpu, bus);
    }

    [Fact]
    public void ARasterWaitEndsOnTheRequestedLine()
    {
        var (cpu, bus) = Machine("AD 12 D0 C9 64 D0 F9 60");          // LDA $D012 / CMP #100 / BNE / RTS
        cpu.Call(0x200);
        Assert.Equal(100, cpu.A);
        Assert.Equal(100, bus.Vic.Raster);
        Assert.InRange(bus.Vic.CycleInLine, 0, 24);                    // caught within the first few cycles of the line
    }

    [Fact]
    public void BadLinesTakeCyclesFromTheProgram()
    {
        // wait for line 100 first so the run starts at the same place whatever the host's speed, then count until line 200
        const string count = "AD 12 D0 C9 64 D0 F9 E6 FB D0 02 E6 FC AD 12 D0 C9 C8 D0 F3 60";
        int Iterations(bool den)
        {
            var (cpu, bus) = Machine(count);
            bus.Write(0xD011, (byte)(den ? 0x1B : 0x0B));
            cpu.Call(0x200);
            return bus.Ram[0xFB] + 256 * bus.Ram[0xFC];
        }
        int on = Iterations(true), off = Iterations(false);
        Assert.True(on < off * 0.95, $"{on} vs {off}");
    }

    [Fact]
    public void SpritesTakeCyclesToo()
    {
        // wait for line 100 first so the run starts at the same place whatever the host's speed, then count until line 200
        const string count = "AD 12 D0 C9 64 D0 F9 E6 FB D0 02 E6 FC AD 12 D0 C9 C8 D0 F3 60";
        int Iterations(byte sprites)
        {
            var (cpu, bus) = Machine(count);
            bus.Write(0xD011, 0x0B);
            bus.Write(0xD015, sprites);
            for (int n = 0; n < 8; n++) bus.Write(0xD001 + n * 2, 120);  // all on lines 121-141
            cpu.Call(0x200);
            return bus.Ram[0xFB] + 256 * bus.Ram[0xFC];
        }
        Assert.True(Iterations(0xFF) < Iterations(0));
    }

    [Fact]
    public void TheClockIsTheHostsAgainAfterTheCall()
    {
        var (cpu, bus) = Machine("60");
        cpu.Call(0x200);
        Assert.False(bus.FollowingCycles);
        Assert.True(bus.ClockIsDefault);
    }

    [Fact]
    public void AReplacedClockIsLeftAlone()
    {
        var (cpu, bus) = Machine("AD 12 D0 60");
        bus.Seconds = () => 0;
        cpu.Call(0x200);
        Assert.False(bus.FollowingCycles);
        Assert.Equal(0, cpu.A);
    }

    [Fact]
    public void ARasterIrqHandlerSeesTheSameLineEveryFrame()
    {
        // main: enable the raster interrupt on line 100 and spin; handler: log $D012, acknowledge, RTI
        var (cpu, bus) = Machine("78 A9 7F 8D 0D DC A9 01 8D 1A D0 A9 64 8D 12 D0 58 4C 11 02");
        Array.Copy(Hex("AD 12 D0 A4 FB 99 00 30 E6 FB A9 01 8D 19 D0 40"), 0, bus.Ram, 0x310, 16);
        bus.Ram[0xFFFE] = 0x10; bus.Ram[0xFFFF] = 0x03;
        bus.Write(0x01, 0x35);
        cpu.Tick = cycles => cycles > 5 * Vic2.CyclesPerFrame;
        cpu.Call(0x200);
        int frames = bus.Ram[0xFB];
        Assert.InRange(frames, 4, 6);
        for (int n = 0; n < frames; n++) Assert.Equal(100, bus.Ram[0x3000 + n]);
    }
}
