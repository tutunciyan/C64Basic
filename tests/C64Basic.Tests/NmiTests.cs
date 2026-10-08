using C64Basic.Core.Machine;

namespace C64Basic.Tests;

public class NmiTests
{
    // CIA 2 timer A fires after 4096 cycles; the main loop waits for the handler to set $FB
    const string TimerSetup = "A9 00 8D 04 DD  A9 10 8D 05 DD  A9 81 8D 0D DD  A9 01 8D 0E DD  A5 FB F0 FC 60";
    const string Handler = "E6 FB AD 0D DD 40";   // INC $FB: LDA $DD0D (acknowledge): RTI

    static void Load(Bus bus, int address, string hex)
    {
        var bytes = Convert.FromHexString(hex.Replace(" ", ""));
        Array.Copy(bytes, 0, bus.Ram, address, bytes.Length);
    }

    [Fact]
    public void Cia2TimerRaisesAnNmiThroughTheRamVector()
    {
        double now = 0;
        var bus = new Bus { Seconds = () => now };
        var cpu = new Cpu6502(bus);
        Load(bus, 0x200, TimerSetup);
        Load(bus, 0x300, Handler);
        bus.Ram[0x318] = 0x00; bus.Ram[0x319] = 0x03;
        cpu.Tick = _ => { now += 0.001; return now > 1; };

        cpu.Call(0x200);

        Assert.Equal(CpuStop.Returned, cpu.StopReason);
        Assert.Equal(1, bus.Ram[0xFB]);
        Assert.True(now < 0.05);
    }

    [Fact]
    public void NmiIgnoresTheInterruptDisableFlag()
    {
        double now = 0;
        var bus = new Bus { Seconds = () => now };
        var cpu = new Cpu6502(bus);
        Load(bus, 0x200, "78" + TimerSetup);           // SEI first
        Load(bus, 0x300, Handler);
        bus.Ram[0x318] = 0x00; bus.Ram[0x319] = 0x03;
        cpu.Tick = _ => { now += 0.001; return now > 1; };

        cpu.Call(0x200);

        Assert.Equal(1, bus.Ram[0xFB]);
    }

    [Fact]
    public void TheNmiFiresOncePerEdgeNotWhileTheLineStaysActive()
    {
        var input = new TestInput();
        var bus = new Bus { Input = input };
        var cpu = new Cpu6502(bus);
        Load(bus, 0x200, "A5 FC F0 FC 60");            // wait for $FC
        Load(bus, 0x300, "E6 FB 40");                  // count NMIs, no acknowledge needed for the key
        bus.Ram[0x318] = 0x00; bus.Ram[0x319] = 0x03;
        int ticks = 0;
        cpu.Tick = _ =>
        {
            ticks++;
            if (ticks == 2) input.Restore = true;       // pressed and held
            if (ticks == 40) bus.Ram[0xFC] = 1;         // let the loop end much later
            return ticks > 100;
        };

        cpu.Call(0x200);

        Assert.Equal(1, bus.Ram[0xFB]);
    }

    [Fact]
    public void ReleasingAndPressingRestoreAgainFiresAgain()
    {
        var input = new TestInput();
        var bus = new Bus { Input = input };
        var cpu = new Cpu6502(bus);
        Load(bus, 0x200, "A5 FC F0 FC 60");
        Load(bus, 0x300, "E6 FB 40");
        bus.Ram[0x318] = 0x00; bus.Ram[0x319] = 0x03;
        int ticks = 0;
        cpu.Tick = _ =>
        {
            ticks++;
            input.Restore = ticks is >= 2 and < 10 or >= 20 and < 30;
            if (ticks == 50) bus.Ram[0xFC] = 1;
            return ticks > 100;
        };

        cpu.Call(0x200);

        Assert.Equal(2, bus.Ram[0xFB]);
    }

    [Fact]
    public void WithTheKernalBankedOutTheHardwareVectorIsUsed()
    {
        double now = 0;
        var bus = new Bus { Seconds = () => now };
        var cpu = new Cpu6502(bus);
        bus.Ram[1] = 0x35;                              // RAM at $E000-$FFFF, I/O visible
        Load(bus, 0x200, TimerSetup);
        Load(bus, 0x300, Handler);
        bus.Ram[0xFFFA] = 0x00; bus.Ram[0xFFFB] = 0x03;
        cpu.Tick = _ => { now += 0.001; return now > 1; };

        cpu.Call(0x200);

        Assert.Equal(1, bus.Ram[0xFB]);
    }

    [Fact]
    public void TheStockHandlerAcknowledgesAndCarriesOn()
    {
        double now = 0;
        var bus = new Bus { Seconds = () => now };
        var cpu = new Cpu6502(bus);
        Load(bus, 0x200, TimerSetup.Replace("A5 FB F0 FC 60", "A5 FB F0 FC 60"));
        int ticks = 0;
        cpu.Tick = _ => { now += 0.001; ticks++; if (ticks == 40) bus.Ram[0xFB] = 1; return now > 1; };

        cpu.Call(0x200);

        Assert.Equal(CpuStop.Returned, cpu.StopReason);   // the default handler did not crash the loop
        Assert.False(bus.Cia2.InterruptPending);
    }

    [Fact]
    public void RunStopRestoreIsTheWarmStart()
    {
        var input = new TestInput().Press(63);          // RUN/STOP held
        var bus = new Bus { Input = input };
        var cpu = new Cpu6502(bus);
        Load(bus, 0x200, "4C 00 02");                   // JMP * forever
        int ticks = 0;
        cpu.Tick = _ => { if (++ticks == 3) input.Restore = true; return ticks > 200; };

        cpu.Call(0x200);

        Assert.Equal(CpuStop.Terminated, cpu.StopReason);
        Assert.True(ticks < 20);
    }

    [Fact]
    public void RestoreAloneDoesNothingWithTheStockHandler()
    {
        var input = new TestInput();
        var bus = new Bus { Input = input };
        var cpu = new Cpu6502(bus);
        Load(bus, 0x200, "4C 00 02");
        int ticks = 0;
        cpu.Tick = _ => { if (++ticks == 3) input.Restore = true; return ticks > 20; };

        cpu.Call(0x200);

        Assert.Equal(CpuStop.Interrupted, cpu.StopReason);
    }

    [Fact]
    public void TheNmiHandlerTailIsEmulated()
    {
        // a user handler that saves nothing and ends with JMP $FEBC after pushing the registers itself
        var input = new TestInput();
        var bus = new Bus { Input = input };
        var cpu = new Cpu6502(bus);
        cpu.Traps[0xFEBC] = c => { c.LeaveInterrupt(); return TrapResult.Continue; };
        Load(bus, 0x200, "A2 55 A5 FC F0 FC 60");       // LDX #$55 first: X must survive the interrupt
        Load(bus, 0x300, "48 8A 48 98 48 A2 00 E6 FB 4C BC FE");   // PHA TXA PHA TYA PHA, LDX #0, INC $FB, JMP $FEBC
        bus.Ram[0x318] = 0x00; bus.Ram[0x319] = 0x03;
        int ticks = 0;
        cpu.Tick = _ => { if (++ticks == 2) input.Restore = true; if (ticks == 4) bus.Ram[0xFC] = 1; return ticks > 50; };

        cpu.Call(0x200);

        Assert.Equal(1, bus.Ram[0xFB]);
        Assert.Equal(0x55, cpu.X);
    }
}
