using C64Basic.Core.Machine;
using C64Basic.Core.Rom;

namespace C64Basic.Tests;

/// <summary>Cartridges (CRT images, the GAME and EXROM lines, bank registers) and the RAM expansion unit.</summary>
public class ExpansionTests
{
    // ---------- building a CRT ----------
    static byte[] Crt(int type, bool gameLow, bool exromLow, params (int Bank, int Load, byte[] Rom)[] chips)
    {
        var output = new List<byte>();
        output.AddRange(System.Text.Encoding.ASCII.GetBytes("C64 CARTRIDGE   "));
        output.AddRange(new byte[] { 0, 0, 0, 0x40, 1, 0, (byte)(type >> 8), (byte)type, (byte)(exromLow ? 0 : 1), (byte)(gameLow ? 0 : 1), 0, 0, 0, 0, 0, 0 });
        var name = new byte[32];
        System.Text.Encoding.ASCII.GetBytes("TEST CART").CopyTo(name, 0);
        output.AddRange(name);
        foreach (var (bank, load, rom) in chips)
        {
            output.AddRange(System.Text.Encoding.ASCII.GetBytes("CHIP"));
            int packet = 16 + rom.Length;
            output.AddRange(new byte[] { (byte)(packet >> 24), (byte)(packet >> 16), (byte)(packet >> 8), (byte)packet, 0, 0 });
            output.AddRange(new byte[] { (byte)(bank >> 8), (byte)bank, (byte)(load >> 8), (byte)load, (byte)(rom.Length >> 8), (byte)rom.Length });
            output.AddRange(rom);
        }
        return output.ToArray();
    }

    static byte[] Filled(int size, byte value)
    {
        var rom = new byte[size];
        Array.Fill(rom, value);
        return rom;
    }

    static Bus Machine(Cartridge? cart = null)
    {
        var bus = new Bus();
        bus.EnableRomMode(Filled(8192, 0xAA), Filled(8192, 0xBB));
        bus.Write(0, 0x2F);
        bus.Write(1, 0x37);
        if (cart != null) bus.AttachCartridge(cart);
        return bus;
    }

    // ---------- the file ----------
    [Fact]
    public void ANormalCartridgeIsRead()
    {
        var cart = Cartridge.FromCrt(Crt(Cartridge.Normal, gameLow: false, exromLow: true, (0, 0x8000, Filled(8192, 0x11))));
        Assert.Equal("TEST CART", cart.Name);
        Assert.True(cart.ExromLow);
        Assert.False(cart.GameLow);
        Assert.False(cart.Ultimax);
        Assert.Equal(0x11, cart.ReadRoml(0x1FFF));
        Assert.Equal(1, cart.Banks);
    }

    [Fact]
    public void ASixteenKilobyteChipIsSplitInRomlAndRomh()
    {
        var rom = new byte[16384];
        Array.Fill(rom, (byte)1, 0, 8192);
        Array.Fill(rom, (byte)2, 8192, 8192);
        var cart = Cartridge.FromCrt(Crt(Cartridge.Normal, gameLow: true, exromLow: true, (0, 0x8000, rom)));
        Assert.Equal(1, cart.ReadRoml(5));
        Assert.Equal(2, cart.ReadRomh(5));
    }

    [Fact]
    public void ABadImageOrAnUnsupportedTypeIsRefused()
    {
        Assert.Throws<InvalidDataException>(() => Cartridge.FromCrt(new byte[100]));
        Assert.Throws<InvalidDataException>(() => Cartridge.FromCrt(Crt(10, false, true, (0, 0x8000, Filled(8192, 0)))));
        var cut = Crt(Cartridge.Normal, false, true, (0, 0x8000, Filled(8192, 0)));
        Assert.Throws<InvalidDataException>(() => Cartridge.FromCrt(cut[..^100]));
    }

    // ---------- the memory map ----------
    [Fact]
    public void An8KCartridgeShowsAtRomlWhileLoramAndHiramAreSet()
    {
        var bus = Machine(Cartridge.FromCrt(Crt(0, false, true, (0, 0x8000, Filled(8192, 0xCC)))));
        Assert.Equal(0xCC, bus.Read(0x8000));
        Assert.Equal(0xAA, bus.Read(0xA000));                  // BASIC is still there
        Assert.Equal(0xBB, bus.Read(0xE000));
        bus.Write(1, 0x36);                                    // LORAM off: RAM at $8000 again, KERNAL still on
        Assert.Equal(0x00, bus.Read(0x8000));
        Assert.Equal(0xBB, bus.Read(0xE000));
    }

    [Fact]
    public void A16KCartridgeReplacesBasic()
    {
        var rom = new byte[16384];
        Array.Fill(rom, (byte)0xC1, 0, 8192);
        Array.Fill(rom, (byte)0xC2, 8192, 8192);
        var bus = Machine(Cartridge.FromCrt(Crt(0, true, true, (0, 0x8000, rom))));
        Assert.Equal(0xC1, bus.Read(0x8000));
        Assert.Equal(0xC2, bus.Read(0xA000));
        bus.Write(1, 0x35);                                    // HIRAM and LORAM off: RAM
        Assert.Equal(0x00, bus.Read(0xA000));
    }

    [Fact]
    public void WritesToTheRomAreaStillReachTheRamUnderIt()
    {
        var bus = Machine(Cartridge.FromCrt(Crt(0, false, true, (0, 0x8000, Filled(8192, 0xCC)))));
        bus.Write(0x8000, 0x42);
        Assert.Equal(0xCC, bus.Read(0x8000));
        Assert.Equal(0x42, bus.Ram[0x8000]);
    }

    [Fact]
    public void UltimaxModeHasRomlRomhAndRamOnlyAtTheBottom()
    {
        var bus = Machine(Cartridge.FromCrt(Crt(0, true, false, (0, 0x8000, Filled(8192, 0xD1)), (0, 0xE000, Filled(8192, 0xD2)))));
        Assert.True(bus.Cart!.Ultimax);
        Assert.Equal(0xD1, bus.Read(0x8000));
        Assert.Equal(0xD2, bus.Read(0xE000));                  // the cartridge is the KERNAL
        Assert.Equal(0xD2, bus.Read(0xFFFC));
        bus.Write(0x0400, 7);
        Assert.Equal(7, bus.Read(0x0400));
        bus.Write(0x2000, 9);                                  // no RAM there
        Assert.Equal(0, bus.Ram[0x2000]);
        bus.Write(0xD020, 3);                                  // the chips are still there
        Assert.Equal(3, bus.Read(0xD020) & 15);
    }

    // ---------- bank registers ----------
    [Fact]
    public void AnOceanCartridgeSwitchesBanksThroughDe00()
    {
        var chips = Enumerable.Range(0, 4).Select(b => (b, 0x8000, Filled(8192, (byte)(0x40 + b)))).ToArray();
        var bus = Machine(Cartridge.FromCrt(Crt(Cartridge.Ocean, false, true, chips)));
        Assert.Equal(0x40, bus.Read(0x8000));
        bus.Write(0xDE00, 2);
        Assert.Equal(0x42, bus.Read(0x9FFF));
        bus.Write(0xDE00, 0x83);
        Assert.Equal(0x43, bus.Read(0x8000));                  // only the low six bits count
        bus.Write(1, 0x37);
        bus.PowerOn();
        Assert.Equal(0x40, bus.Read(0x8000));                  // a reset goes back to bank 0
    }

    [Fact]
    public void AMagicDeskCartridgeCanSwitchItselfOff()
    {
        var chips = Enumerable.Range(0, 2).Select(b => (b, 0x8000, Filled(8192, (byte)(0x50 + b)))).ToArray();
        var bus = Machine(Cartridge.FromCrt(Crt(Cartridge.MagicDesk, false, true, chips)));
        bus.Write(0xDE00, 1);
        Assert.Equal(0x51, bus.Read(0x8000));
        bus.Write(0xDE00, 0x80);                               // bit 7: off
        Assert.Equal(0, bus.Read(0x8000));
        Assert.False(bus.Cart!.ExromLow);
    }

    [Fact]
    public void AGameSystemCartridgeTakesItsBankFromTheAddress()
    {
        var chips = Enumerable.Range(0, 8).Select(b => (b, 0x8000, Filled(8192, (byte)(0x60 + b)))).ToArray();
        var bus = Machine(Cartridge.FromCrt(Crt(Cartridge.GameSystem, false, true, chips)));
        Assert.Equal(0x60, bus.Read(0x8000));
        bus.Write(0xDE05, 0);
        Assert.Equal(0x65, bus.Read(0x8000));
    }

    [Fact]
    public void AnEasyFlashStartsInUltimaxAndSwitchesToSixteenKilobytesThroughDe02()
    {
        var chips = new List<(int, int, byte[])>();
        for (int b = 0; b < 2; b++)
        {
            chips.Add((b, 0x8000, Filled(8192, (byte)(0x70 + b))));
            chips.Add((b, 0xA000, Filled(8192, (byte)(0x80 + b))));
        }
        chips.Add((0, 0xE000, Filled(8192, 0x99)));            // never reached: bank 0's ROMH is at $A000 here, so make the boot ROM bank 0 too
        var bus = Machine(Cartridge.FromCrt(Crt(Cartridge.EasyFlash, true, false, chips.ToArray())));
        Assert.True(bus.Cart!.Ultimax);
        Assert.Equal(0x70, bus.Read(0x8000));
        bus.Write(0xDF10, 0x5A);                               // the 256 bytes of RAM
        Assert.Equal(0x5A, bus.Read(0xDF10));
        bus.Write(0xDE00, 1);
        bus.Write(0xDE02, 7);                                  // M=1, X=1, G=1: 16K
        Assert.False(bus.Cart.Ultimax);
        Assert.Equal(0x71, bus.Read(0x8000));
        Assert.Equal(0x81, bus.Read(0xA000));
        bus.Write(0xDE02, 4);                                  // M=1, X=0, G=0: both lines high: the cartridge is off
        Assert.Equal(0xAA, bus.Read(0xA000));
        Assert.Equal(0, bus.Read(0x8000));
    }

    [Fact]
    public void ACartridgeStateComesBack()
    {
        var chips = Enumerable.Range(0, 4).Select(b => (b, 0x8000, Filled(8192, (byte)(0x40 + b)))).ToArray();
        var crt = Crt(Cartridge.Ocean, false, true, chips);
        var bus = Machine(Cartridge.FromCrt(crt));
        bus.Write(0xDE00, 3);
        var stream = new MemoryStream();
        using (var w = new BinaryWriter(stream, System.Text.Encoding.UTF8, true)) bus.Cart!.SaveState(w);
        var other = Machine(Cartridge.FromCrt(crt));
        stream.Position = 0;
        using (var r = new BinaryReader(stream)) other.Cart!.LoadState(r);
        Assert.Equal(0x43, other.Read(0x8000));
    }

    // ---------- the RAM expansion unit ----------
    static (Bus Bus, Reu Reu, int[] Halted) WithReu(int kb = 512)
    {
        var bus = new Bus();
        var halted = new[] { 0 };
        var reu = new Reu(bus, kb) { Halt = n => halted[0] += n };
        bus.AttachReu(reu);
        return (bus, reu, halted);
    }

    static void Setup(Bus bus, int c64, int reu, int length, int command, int addressControl = 0)
    {
        bus.Write(0xDF02, (byte)c64); bus.Write(0xDF03, (byte)(c64 >> 8));
        bus.Write(0xDF04, (byte)reu); bus.Write(0xDF05, (byte)(reu >> 8)); bus.Write(0xDF06, (byte)(reu >> 16));
        bus.Write(0xDF07, (byte)length); bus.Write(0xDF08, (byte)(length >> 8));
        bus.Write(0xDF0A, (byte)addressControl);
        bus.Write(0xDF01, (byte)command);
    }

    [Fact]
    public void AnReuTakesBlocksFromTheC64AndGivesThemBack()
    {
        var (bus, reu, halted) = WithReu();
        for (int i = 0; i < 100; i++) bus.Ram[0x4000 + i] = (byte)(i * 3);
        Setup(bus, 0x4000, 0x012345, 100, 0x90);               // execute, no FF00 trigger, C64 -> REU
        Assert.Equal(0x00, reu.Memory[0x012345]);
        Assert.Equal(1 * 3, reu.Memory[0x012346]);
        Assert.Equal((byte)(297 - 256), reu.Memory[0x012345 + 99]);
        Assert.Equal(104, halted[0]);                          // a cycle a byte, a few to start
        Array.Clear(bus.Ram, 0x4000, 100);
        Setup(bus, 0x4000, 0x012345, 100, 0x91);               // REU -> C64
        for (int i = 0; i < 100; i++) Assert.Equal((byte)(i * 3), bus.Ram[0x4000 + i]);
    }

    [Fact]
    public void TheAddressesAdvanceUnlessAutoloadPutsThemBack()
    {
        var (bus, _, _) = WithReu();
        Setup(bus, 0x4000, 0x100, 16, 0x90);
        Assert.Equal(0x10, bus.Read(0xDF02));                  // 0x4000 + 16
        Assert.Equal(0x40, bus.Read(0xDF03));
        Assert.Equal(0x10, bus.Read(0xDF04) & 0xF0);
        Assert.Equal(1, bus.Read(0xDF07));                     // the length register reads 1 after a transfer
        Setup(bus, 0x4000, 0x100, 16, 0xB0);                   // with autoload (bit 5)
        Assert.Equal(0x00, bus.Read(0xDF02));
        Assert.Equal(0x40, bus.Read(0xDF03));
        Assert.Equal(16, bus.Read(0xDF07));
    }

    [Fact]
    public void FixedAddressesRepeatOneByte()
    {
        var (bus, reu, _) = WithReu();
        bus.Ram[0x5000] = 0x77;
        Setup(bus, 0x5000, 0x200, 8, 0x90, addressControl: 0x80);   // the C64 address stays: one byte, eight times
        for (int i = 0; i < 8; i++) Assert.Equal(0x77, reu.Memory[0x200 + i]);
        reu.Memory[0x300] = 0x66;
        Setup(bus, 0x6000, 0x300, 8, 0x91, addressControl: 0x40);   // the REU address stays: fill
        for (int i = 0; i < 8; i++) Assert.Equal(0x66, bus.Ram[0x6000 + i]);
    }

    [Fact]
    public void SwapExchangesTheTwoBlocks()
    {
        var (bus, reu, _) = WithReu();
        bus.Ram[0x4000] = 1; bus.Ram[0x4001] = 2;
        reu.Memory[0x10] = 9; reu.Memory[0x11] = 8;
        Setup(bus, 0x4000, 0x10, 2, 0x92);
        Assert.Equal(9, bus.Ram[0x4000]); Assert.Equal(8, bus.Ram[0x4001]);
        Assert.Equal(1, reu.Memory[0x10]); Assert.Equal(2, reu.Memory[0x11]);
    }

    [Fact]
    public void VerifyStopsAtTheFirstDifferenceAndSetsTheErrorFlag()
    {
        var (bus, reu, _) = WithReu();
        for (int i = 0; i < 10; i++) { bus.Ram[0x4000 + i] = (byte)i; reu.Memory[i] = (byte)i; }
        Setup(bus, 0x4000, 0, 10, 0x93);
        Assert.Equal(0x40, bus.Read(0xDF00) & 0x60);           // end of block, no error
        reu.Memory[6] = 99;
        Setup(bus, 0x4000, 0, 10, 0x93);
        Assert.Equal(0x20, bus.Read(0xDF00) & 0x60);           // error, not finished
        Assert.Equal(0, bus.Read(0xDF00) & 0x60);              // the flags clear when read
    }

    [Fact]
    public void TheTransferWrapsInTheReusMemoryAndALengthOfZeroIs64K()
    {
        var (bus, reu, _) = WithReu(128);
        bus.Ram[0xFFFF] = 5;
        Setup(bus, 0xFFFF, 0x1FFFF, 2, 0x90);                  // the last byte of 128 KB, then round to the first
        Assert.Equal(5, reu.Memory[0x1FFFF]);
        Assert.Equal(bus.Ram[0], reu.Memory[0]);
        Setup(bus, 0, 0, 0, 0x90);
        Assert.Equal(0x40, bus.Read(0xDF00) & 0x40);
        Assert.Equal(0x00, bus.Read(0xDF02));                  // 64K later the C64 address is back where it began
    }

    [Fact]
    public void TheEndOfBlockInterruptNeedsItsMask()
    {
        var (bus, reu, _) = WithReu();
        Setup(bus, 0x4000, 0, 4, 0x90);
        Assert.False(reu.IrqPending);
        bus.Write(0xDF09, 0xC0);                               // interrupt enable + end of block
        Setup(bus, 0x4000, 0, 4, 0x90);
        bus.Write(0xDF09, 0xC0);
        Assert.True(reu.IrqPending);
        Assert.True(bus.IrqLine);
        bus.Read(0xDF00);
        Assert.False(reu.IrqPending);
    }

    [Fact]
    public void ACommandWithoutTheNoTriggerBitWaitsForTheWriteToFf00()
    {
        var (bus, reu, _) = WithReu();
        bus.Ram[0x4000] = 0x33;
        Setup(bus, 0x4000, 0x50, 1, 0x80);                     // execute, FF00 trigger enabled
        Assert.Equal(0, reu.Memory[0x50]);
        bus.Write(0xFF00, 0);
        Assert.Equal(0x33, reu.Memory[0x50]);
    }

    [Fact]
    public void AnReuReportsItsSizeInTheVersionBitAndTheBankBits()
    {
        var (small, _, _) = WithReu(128);
        var (big, _, _) = WithReu(512);
        Assert.Equal(0, small.Read(0xDF00) & 0x10);
        Assert.Equal(0x10, big.Read(0xDF00) & 0x10);
        big.Write(0xDF06, 0x05);
        Assert.Equal(0xF8 | 5, big.Read(0xDF06));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Reu(new Bus(), 100));
    }

    [Fact]
    public void AnReusStateComesBack()
    {
        var (bus, reu, _) = WithReu(256);
        reu.Memory[1234] = 0x5E;
        Setup(bus, 0x4000, 0x20, 4, 0x90);
        var stream = new MemoryStream();
        using (var w = new BinaryWriter(stream, System.Text.Encoding.UTF8, true)) reu.SaveState(w);
        var (_, other, _) = WithReu(256);
        using (var r = new BinaryReader(new MemoryStream(stream.ToArray()))) other.LoadState(r);
        Assert.Equal(0x5E, other.Memory[1234]);
        Assert.Equal(reu.Read(0xDF04), other.Read(0xDF04));
        var (_, wrongSize, _) = WithReu(512);
        using (var r = new BinaryReader(new MemoryStream(stream.ToArray()))) Assert.Throws<InvalidDataException>(() => wrongSize.LoadState(r));
    }

    // ---------- with the real KERNAL ----------
    [RomFact]
    public void ACartridgeWithTheCbm80SignatureIsStartedByTheKernal()
    {
        var rom = new byte[8192];
        rom[0] = 0x09; rom[1] = 0x80;                          // cold start vector
        rom[2] = 0x09; rom[3] = 0x80;                          // warm start vector
        new byte[] { 0xC3, 0xC2, 0xCD, 0x38, 0x30 }.CopyTo(rom, 4);                  // "CBM80" as the KERNAL looks for it
        new byte[] { 0xA9, 0x05, 0x8D, 0x20, 0xD0, 0x4C, 0x0E, 0x80 }.CopyTo(rom, 9);   // LDA #5 : STA $D020 : JMP *
        var m = new RomMachine(TestRoms.Find()!);
        m.InsertCartridge(Cartridge.FromCrt(Crt(0, false, true, (0, 0x8000, rom))));
        m.RunSeconds(3.0);
        Assert.Equal(5, m.Bus.Read(0xD020) & 15);
        Assert.Equal(0x800E, m.Cpu.PC);
        Assert.DoesNotContain("COMMODORE 64", m.ScreenText());   // BASIC never started
        m.InsertCartridge(null);                               // pulled out: the machine boots to the banner
        m.RunSeconds(3.5);
        Assert.Contains("COMMODORE 64 BASIC", m.ScreenText());
    }

    [RomFact]
    public void BasicCanUseAnReuThroughItsRegisters()
    {
        var m = new RomMachine(TestRoms.Find()!);
        m.InsertReu(512);
        m.RunSeconds(3.5);
        // stash 42 at $C000 in the REU, clear it, bring it back
        m.Type("POKE49152,42:POKE57090,0:POKE57091,192:POKE57092,0:POKE57093,0\r");
        m.Type("POKE57094,0:POKE57095,1:POKE57096,0:POKE57089,144\r");
        m.Type("POKE49152,0:POKE57090,0:POKE57091,192:POKE57092,0:POKE57093,0\r");
        m.Type("POKE57095,1:POKE57096,0:POKE57089,145\r");
        m.Type("PRINT\"GOT\";PEEK(49152)\r");
        Assert.True(m.RunUntil(() => m.ScreenText().Contains("GOT 42"), 10), m.ScreenText());
        Assert.Equal(512, m.Bus.Expansion!.SizeKilobytes);
        Assert.Null(m.HaltReason);
    }

    [RomFact]
    public void AStateKeepsTheReuAndRefusesAMachineWithoutOne()
    {
        var m = new RomMachine(TestRoms.Find()!);
        m.InsertReu(256);
        m.RunSeconds(3.5);
        m.Bus.Expansion!.Memory[777] = 0x42;
        byte[] state = m.SaveState();
        var other = new RomMachine(TestRoms.Find()!);
        other.InsertReu(256);
        other.LoadState(state);
        Assert.Equal(0x42, other.Bus.Expansion!.Memory[777]);
        Assert.Throws<InvalidDataException>(() => new RomMachine(TestRoms.Find()!).LoadState(state));
    }
}
