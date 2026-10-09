using C64Basic.Core.Disk;
using C64Basic.Core.Machine;
using C64Basic.Core.Rom;

namespace C64Basic.Tests;

/// <summary>The C64 running its real BASIC and KERNAL ROMs beside a real 1541. Most of these need the ROM dumps in <c>roms/</c>.</summary>
public class RomMachineTests
{
    static RomMachine? Boot(bool drive = true)
    {
        var roms = TestRoms.Find();
        if (roms == null) return null;
        var m = new RomMachine(roms, drive);
        Assert.True(m.RunUntil(() => m.ScreenText().Contains("READY."), 10), "boots to the BASIC prompt:\n" + m.ScreenText());
        return m;
    }

    /// <summary>A BASIC program as a PRG file: 10 PRINT"HI FROM DISK".</summary>
    static byte[] HelloProgram()
    {
        var text = "HI FROM DISK";
        var bytes = new List<byte> { 0x01, 0x08 };
        int next = 0x0801 + 2 + 2 + 1 + 1 + text.Length + 1 + 1;
        bytes.AddRange(new byte[] { (byte)next, (byte)(next >> 8), 10, 0, 0x99, 0x22 });
        bytes.AddRange(System.Text.Encoding.ASCII.GetBytes(text));
        bytes.AddRange(new byte[] { 0x22, 0, 0, 0 });
        return bytes.ToArray();
    }

    // ---------- the memory map (fake ROMs, no dumps needed) ----------
    static Bus RomBus()
    {
        var basic = new byte[8192]; Array.Fill(basic, (byte)0xAA);
        var kernal = new byte[8192]; Array.Fill(kernal, (byte)0xEE);
        var bus = new Bus();
        bus.EnableRomMode(basic, kernal);
        return bus;
    }

    [Fact]
    public void AtPowerOnAllRomsAndTheIoAreVisibleBecauseThePortPinsAreInputs()
    {
        var bus = RomBus();
        Assert.True(bus.RomMode);
        Assert.Equal(0xAA, bus.Read(0xA000));
        Assert.Equal(0xAA, bus.Read(0xBFFF));
        Assert.Equal(0xEE, bus.Read(0xE000));
        Assert.Equal(0xEE, bus.Read(0xFFFF));
        Assert.Equal(0x37, bus.Read(1));                         // the input pins read high
    }

    [Fact]
    public void TheProcessorPortSwitchesTheRomsAndTheIo()
    {
        var bus = RomBus();
        bus.Write(0, 0x2F);                                      // the KERNAL's setting: bits 0-2 and 5 are outputs
        bus.Ram[0xA000] = 0x11; bus.Ram[0xE000] = 0x22; bus.Ram[0xD000] = 0x33;

        bus.Write(1, 0x37);                                      // BASIC, KERNAL, I/O
        Assert.Equal(0xAA, bus.Read(0xA000));
        Assert.Equal(0xEE, bus.Read(0xE000));
        Assert.NotEqual(0x33, bus.Read(0xD000));                 // the VIC-II, not the RAM underneath

        bus.Write(1, 0x36);                                      // no BASIC
        Assert.Equal(0x11, bus.Read(0xA000));
        Assert.Equal(0xEE, bus.Read(0xE000));

        bus.Write(1, 0x35);                                      // RAM and I/O only
        Assert.Equal(0x11, bus.Read(0xA000));
        Assert.Equal(0x22, bus.Read(0xE000));
        Assert.NotEqual(0x33, bus.Read(0xD000));

        bus.Write(1, 0x34);                                      // all RAM
        Assert.Equal(0x11, bus.Read(0xA000));
        Assert.Equal(0x22, bus.Read(0xE000));
        Assert.Equal(0x33, bus.Read(0xD000));

        bus.Write(1, 0x33);                                      // BASIC, KERNAL, character ROM
        Assert.Equal(0xAA, bus.Read(0xA000));
        Assert.Equal(bus.CharacterRom[0], bus.Read(0xD000));
    }

    [Fact]
    public void WritesAlwaysReachTheRamUnderARom()
    {
        var bus = RomBus();
        bus.Write(0, 0x2F); bus.Write(1, 0x37);
        bus.Write(0xA000, 0x5A);
        bus.Write(0xE000, 0xA5);
        Assert.Equal(0xAA, bus.Read(0xA000));                    // still the ROM
        Assert.Equal(0x5A, bus.Ram[0xA000]);
        Assert.Equal(0xA5, bus.Ram[0xE000]);
        bus.Write(1, 0x34);
        Assert.Equal(0x5A, bus.Read(0xA000));
        Assert.Equal(0xA5, bus.Read(0xE000));
    }

    [Fact]
    public void ThePortReadsBackOutputsAsWrittenAndInputsHigh()
    {
        var bus = RomBus();
        bus.Write(0, 0x2F);
        bus.Write(1, 0x00);
        Assert.Equal(0x10, bus.Read(1));                         // only the cassette sense input (bit 4) reads high
        bus.Write(1, 0x07);
        Assert.Equal(0x17, bus.Read(1));
    }

    [Fact]
    public void WithoutRomModeTheBusIsUnchanged()
    {
        var bus = new Bus();
        Assert.False(bus.RomMode);
        bus.Ram[0xA000] = 0x77;
        Assert.Equal(0x77, bus.Read(0xA000));                    // no ROM image: the interpreter's memory map
    }

    [Fact]
    public void PowerOnClearsTheChipsAndTheMemory()
    {
        var bus = RomBus();
        bus.Ram[0x400] = 0x41;
        bus.Cia1.Write(0xDC0E, 0x01);
        bus.PowerOn();
        Assert.Equal(0, bus.Ram[0x400]);
        Assert.Equal(0, bus.Cia1.Read(0xDC0E));
        Assert.Equal(0xFF, bus.Cia1.Read(0xDC04));               // timer A at its power-on value, stopped
        Assert.False(bus.IrqLine);
    }

    // ---------- the real ROMs ----------
    [Fact]
    public void TheMachineBootsToTheBasicPrompt()
    {
        var m = Boot();
        if (m == null) return;
        string screen = m.ScreenText();
        Assert.Contains("**** COMMODORE 64 BASIC V2 ****", screen);
        Assert.Contains("64K RAM SYSTEM  38911 BASIC BYTES FREE", screen);
        Assert.Equal(CpuStop.None, m.Cpu.StopReason);
        Assert.Null(m.HaltReason);
    }

    [Fact]
    public void ThereIsNoDriveInTheMachineWhenNoneIsAsked()
    {
        var m = Boot(drive: false);
        if (m == null) return;
        Assert.Null(m.Drive);
        m.Type("LOAD\"$\",8\r");
        Assert.True(m.RunUntil(() => m.ScreenText().Contains("DEVICE NOT PRESENT"), 5), m.ScreenText());
    }

    [Fact]
    public void BasicRunsOnTheRealInterpreter()
    {
        var m = Boot();
        if (m == null) return;
        m.Type("PRINT 6*7\r10 FOR I=1 TO 3:PRINT I*I:NEXT\rRUN\r");
        Assert.True(m.RunUntil(() => m.ScreenText().Split('\n').Count(l => l == "READY.") >= 3, 5), m.ScreenText());
        string screen = m.ScreenText();
        Assert.Contains("\n 42\n", screen);
        Assert.Contains("\n 1\n 4\n 9\n", screen);
    }

    [Fact]
    public void TheRealKernalInterruptKeepsTheJiffyClock()
    {
        var m = Boot();
        if (m == null) return;
        int Jiffies() => m.Bus.Ram[0xA0] << 16 | m.Bus.Ram[0xA1] << 8 | m.Bus.Ram[0xA2];
        int start = Jiffies();
        m.RunSeconds(2.0);
        int ticks = Jiffies() - start;
        Assert.InRange(ticks, 118, 122);                         // 60 interrupts a second from CIA 1's timer
    }

    [Fact]
    public void KeysPressedOnTheMatrixAreScannedByTheKernal()
    {
        var m = Boot();
        if (m == null) return;
        m.Input.SetKey(10, true);                                // A
        m.RunSeconds(0.15);
        m.Input.SetKey(10, false);
        m.RunSeconds(0.1);
        m.Input.SetKey(15, true); m.Input.SetKey(10, true);      // left shift + A: a graphics symbol, not an A
        m.RunSeconds(0.15);
        m.Input.ReleaseAllKeys();
        m.RunSeconds(0.1);
        string last = m.ScreenText().Split('\n').Last(l => l.Length > 0 && l != "READY.");
        Assert.StartsWith("A", last);
        Assert.Equal(2, last.Length);                            // plus the graphics character
    }

    [Fact]
    public void RunStopWithRestoreIsTheWarmStart()
    {
        var m = Boot();
        if (m == null) return;
        m.Type("10 GOTO 10\rRUN\r");
        m.RunSeconds(0.5);
        Assert.DoesNotContain("READY.", m.ScreenText().Split('\n').Last());
        m.Input.SetKey(63, true);
        m.Input.SetRestore(true);
        m.RunSeconds(0.3);
        m.Input.SetRestore(false);
        m.Input.SetKey(63, false);
        Assert.True(m.RunUntil(() => m.ScreenText().Split('\n').Last() == "READY.", 2), m.ScreenText());
    }

    [Fact]
    public void LoadAndListTheDirectoryThroughTheRealSerialRoutines()
    {
        var m = Boot();
        if (m == null) return;
        var image = D64Image.Create("ROM MODE", "RM");
        image.Write("HELLO", FileType.Prg, HelloProgram(), replace: false);
        m.MountDisk(image.ToArray());
        m.Type("LOAD\"$\",8\rLIST\r");
        Assert.True(m.RunUntil(() => m.ScreenText().Contains("BLOCKS FREE"), 15), m.ScreenText());
        string screen = m.ScreenText();
        Assert.Contains("0 \"ROM MODE        \" RM 2A", screen);
        Assert.Contains("\"HELLO\"            PRG", screen);
        Assert.Contains("663 BLOCKS FREE.", screen);
    }

    [Fact]
    public void ALoadedProgramRuns()
    {
        var m = Boot();
        if (m == null) return;
        var image = D64Image.Create("ROM MODE", "RM");
        image.Write("HELLO", FileType.Prg, HelloProgram(), replace: false);
        m.MountDisk(image.ToArray());
        m.Type("LOAD\"HELLO\",8\rRUN\r");
        Assert.True(m.RunUntil(() => m.ScreenText().Contains("HI FROM DISK"), 15), m.ScreenText());
        Assert.True(m.RunUntil(() => m.ScreenText().Split('\n').Last() == "READY.", 2));
    }

    [Fact]
    public void AMissingFileGivesTheDriveErrorAndAnEmptyDriveANotReadyError()
    {
        var m = Boot();
        if (m == null) return;
        m.MountDisk(D64Image.Create("EMPTY", "EE").ToArray());
        m.Type("LOAD\"NOPE\",8\r");
        Assert.True(m.RunUntil(() => m.ScreenText().Contains("FILE NOT FOUND"), 15), m.ScreenText());
        m.Type("10 OPEN 15,8,15:INPUT#15,A,B$:PRINT A;B$:CLOSE 15\rRUN\r");
        Assert.True(m.RunUntil(() => m.ScreenText().Contains("62 FILE NOT FOUND"), 10), m.ScreenText());
    }

    [Fact]
    public void ASwappedDiskIsNoticedBecauseTheWriteProtectSensorFlickers()
    {
        var m = Boot();
        if (m == null) return;
        var one = D64Image.Create("DISK ONE", "11");
        var two = D64Image.Create("DISK TWO", "22");
        m.MountDisk(one.ToArray());
        m.Type("LOAD\"$\",8\rLIST\r");
        Assert.True(m.RunUntil(() => m.ScreenText().Contains("DISK ONE"), 15), m.ScreenText());
        m.MountDisk(two.ToArray());
        m.Type("LOAD\"$\",8\rLIST\r");
        Assert.True(m.RunUntil(() => m.ScreenText().Contains("DISK TWO"), 15), m.ScreenText());
    }

    [Fact]
    public void AProgramIsSavedToTheDiskByTheRealDos()
    {
        var m = Boot();
        if (m == null) return;
        m.MountDisk(D64Image.Create("WRITE TEST", "WT").ToArray());
        GcrDisk? written = null;
        m.DiskWritten += d => written = d;
        m.Type("10 PRINT\"SAVED\"\rSAVE\"MYPROG\",8\r");
        Assert.True(m.RunUntil(() => written != null, 30), "the drive wrote and went back to reading:\n" + m.ScreenText());
        m.RunSeconds(1);

        var (bytes, report) = written!.ToD64();
        Assert.True(report.Clean, report.ToString());
        var image = new D64Image(bytes);
        var file = image.Read("MYPROG");
        Assert.Equal(new byte[] { 0x01, 0x08 }, file.Data.Take(2).ToArray());
        Assert.Contains(image.Directory(), e => e.Name == "MYPROG" && e.Type == FileType.Prg);

        // and it loads again, by the same drive, from the sectors it wrote
        m.Type("NEW\rLOAD\"MYPROG\",8\rLIST\r");
        Assert.True(m.RunUntil(() => m.ScreenText().Contains("10 PRINT\"SAVED\""), 20), m.ScreenText());
    }

    [Fact]
    public void AD64FileIsSavedBackWhenTheDriveWritesToItAndAG64FileIsNot()
    {
        var m = Boot();
        if (m == null) return;
        string dir = Path.Combine(Path.GetTempPath(), "c64rom-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            // a D64 file that does not exist yet is created blank, mounted, and written back after a SAVE
            string d64Path = Path.Combine(dir, "work.d64");
            m.MountDiskFile(d64Path);
            Assert.True(File.Exists(d64Path));
            Assert.True(m.DiskSavesChanges);
            int writes = 0;
            m.DiskWritten += _ => writes++;
            m.Type("10 PRINT\"FILE\"\rSAVE\"KEEP\",8\r");
            Assert.True(m.RunUntil(() => writes > 0, 30), m.ScreenText());
            m.RunSeconds(0.5);
            Assert.Null(m.SaveError);
            var saved = new D64Image(File.ReadAllBytes(d64Path));
            Assert.Contains(saved.Directory(), e => e.Name == "KEEP");
            Assert.False(File.Exists(d64Path + ".tmp"));

            // a G64 is someone else's raw tracks: the drive may write to its copy in memory, the file stays as it was
            string g64Path = Path.Combine(dir, "raw.g64");
            File.WriteAllBytes(g64Path, G64Image.Encode(D64Image.Create("RAW", "RW").ToArray()));
            var before = File.ReadAllBytes(g64Path);
            m.MountDiskFile(g64Path);
            Assert.False(m.DiskSavesChanges);
            writes = 0;
            m.Type("SAVE\"LOST\",8\r");
            Assert.True(m.RunUntil(() => writes > 0, 30), m.ScreenText());
            m.RunSeconds(0.5);
            Assert.Equal(before, File.ReadAllBytes(g64Path));
            Assert.Contains(new D64Image(m.Drive!.Mechanics.Disk!.ToD64().Disk).Directory(), e => e.Name == "LOST");
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void ATapeImageOrAProgramFileIsMountedAsADisk()
    {
        var m = Boot();
        if (m == null) return;
        string dir = Path.Combine(Path.GetTempPath(), "c64rom-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var tape = new T64Image(null, "MY TAPE");
            tape.Write("HELLO", FileType.Prg, HelloProgram(), replace: false);
            string t64 = Path.Combine(dir, "game.t64");
            File.WriteAllBytes(t64, tape.ToArray());
            m.MountDiskFile(t64);
            Assert.False(m.DiskSavesChanges);
            m.Type("LOAD\"HELLO\",8\rRUN\r");
            Assert.True(m.RunUntil(() => m.ScreenText().Contains("HI FROM DISK"), 15), m.ScreenText());

            string prg = Path.Combine(dir, "single.prg");
            File.WriteAllBytes(prg, HelloProgram());
            m.MountDiskFile(prg);
            m.Type("NEW\rLOAD\"SINGLE\",8\rRUN\r");
            Assert.True(m.RunUntil(() => m.ScreenText().Split('\n').Count(l => l == "HI FROM DISK") >= 1 && m.ScreenText().Contains("SEARCHING FOR SINGLE"), 15), m.ScreenText());
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void ADiskIsFormattedByTheRealDos()
    {
        var m = Boot();
        if (m == null) return;
        m.MountDisk(D64Image.Create("OLD", "00").ToArray());
        // reading the error channel waits for the drive to finish: the format writes every track
        m.Type("10 OPEN 15,8,15,\"N0:FRESH DISK,AB\":INPUT#15,A,B$:PRINT \"FMT\";A;B$:CLOSE 15\rRUN\r");
        Assert.True(m.RunUntil(() => m.ScreenText().Contains("FMT 0 OK"), 120), m.ScreenText());
        var (bytes, report) = m.Drive!.Mechanics.Disk!.ToD64();
        Assert.True(report.Clean, report.ToString());
        var image = new D64Image(bytes);
        Assert.Equal("FRESH DISK", image.Title);
        Assert.Equal("AB", image.Id);
        Assert.Equal(664, image.BlocksFree);
        m.Type("LOAD\"$\",8\rLIST\r");
        Assert.True(m.RunUntil(() => m.ScreenText().Contains("664 BLOCKS FREE"), 20), m.ScreenText());
    }

    [Fact]
    public void ARunIsTheSameEveryTime()
    {
        var roms = TestRoms.Find();
        if (roms == null) return;
        string Run()
        {
            var m = new RomMachine(roms);
            m.RunSeconds(3.5);
            m.Type("PRINT TI\r");
            m.RunSeconds(0.3);
            return $"{m.Cpu.PC:X4} {m.Cpu.A:X2} {m.Cpu.X:X2} {m.Cpu.Y:X2} {m.Cpu.Cycles} {m.Drive!.Cpu.PC:X4} {m.Drive.Cycles}\n{m.ScreenText()}";
        }
        Assert.Equal(Run(), Run());
    }

    // ---------- saved state ----------
    static string Signature(RomMachine m) =>
        $"{m.Cpu.PC:X4} {m.Cpu.A:X2} {m.Cpu.X:X2} {m.Cpu.Y:X2} {m.Cpu.SP:X2} {m.Cpu.Cycles} | " +
        $"{m.Drive!.Cpu.PC:X4} {m.Drive.Cpu.A:X2} {m.Drive.Cycles} {m.Drive.Mechanics.Track} {m.Drive.Mechanics.BytesRead}\n{m.ScreenText()}";

    [Fact]
    public void ASavedStateRunsOnExactlyAsTheOriginalDid()
    {
        var m = Boot();
        if (m == null) return;
        m.Type("10 PRINT\"STATE\";:GOTO 10\rRUN\r");
        m.RunSeconds(0.5);
        byte[] state = m.SaveState();

        m.RunSeconds(1.0);
        string original = Signature(m);
        Assert.Contains("STATESTATE", original);

        m.LoadState(state);
        m.RunSeconds(1.0);
        Assert.Equal(original, Signature(m));
    }

    [Fact]
    public void AStateLoadsIntoAFreshMachineWithItsDiskAndProgram()
    {
        var m = Boot();
        if (m == null) return;
        var image = D64Image.Create("IN STATE", "IS");
        image.Write("HELLO", FileType.Prg, HelloProgram(), replace: false);
        m.MountDisk(image.ToArray());
        m.Type("LOAD\"HELLO\",8\r");
        Assert.True(m.RunUntil(() => m.ScreenText().Split('\n').Count(l => l == "READY.") >= 2, 15), m.ScreenText());
        byte[] state = m.SaveState();
        m.Type("RUN\r");
        m.RunSeconds(1);
        string original = Signature(m);

        var other = new RomMachine(TestRoms.Find()!);
        other.LoadState(state);
        other.Type("RUN\r");
        other.RunSeconds(1);
        Assert.Equal(original, Signature(other));
        Assert.Contains("HI FROM DISK", other.ScreenText());
    }

    [Fact]
    public void AStateTakenWhileTheDriveIsLoadingAFileFinishesTheLoad()
    {
        var m = Boot();
        if (m == null) return;
        var payload = new byte[6000];
        for (int i = 0; i < payload.Length; i++) payload[i] = (byte)(i * 7);
        var program = new byte[payload.Length + 2];
        program[0] = 0x00; program[1] = 0x40;
        payload.CopyTo(program, 2);
        var image = D64Image.Create("BIGLOAD", "BL");
        image.Write("BIG", FileType.Prg, program, replace: false);
        m.MountDisk(image.ToArray());
        m.Type("LOAD\"BIG\",8,1\r");
        m.RunUntil(() => m.Drive!.Mechanics.BytesRead > 20000 && m.Drive.Mechanics.Track != 1, 30);   // the drive is busy with the file
        byte[] state = m.SaveState();
        Assert.True(m.RunUntil(() => m.ScreenText().Split('\n').Count(l => l == "READY.") >= 2, 30), m.ScreenText());

        var other = new RomMachine(TestRoms.Find()!);
        other.LoadState(state);
        other.Bus.Ram.AsSpan(0x4000, payload.Length).Clear();       // so only the restored run can bring the file back
        Assert.True(other.RunUntil(() => other.ScreenText().Split('\n').Count(l => l == "READY.") >= 2, 30), other.ScreenText());
        Assert.Equal(payload, other.Bus.Ram.AsSpan(0x4000, payload.Length).ToArray());

        // the two machines followed the same course: brought to the same moment they are the same machine
        long common = Math.Max(m.Cycles, other.Cycles) + 50_000;
        m.RunSeconds((common - m.Cycles) / RomMachine.ClockHz);
        other.RunSeconds((common - other.Cycles) / RomMachine.ClockHz);
        Assert.Equal(Signature(m), Signature(other));
    }

    [Fact]
    public void AStateThatIsNotOneOrDoesNotFitIsRefused()
    {
        var m = Boot();
        if (m == null) return;
        Assert.Throws<InvalidDataException>(() => m.LoadState(new byte[] { 1, 2, 3 }));
        Assert.ThrowsAny<IOException>(() => m.LoadState(m.SaveState().AsSpan(0, 100).ToArray()));
        var noDrive = new RomMachine(TestRoms.Find()!, withDrive: false);
        byte[] withDrive = m.SaveState();
        Assert.Throws<InvalidDataException>(() => noDrive.LoadState(withDrive));
        Assert.Throws<InvalidDataException>(() => m.LoadState(noDrive.SaveState()));
        // a refused state leaves the machine as it was
        Assert.Contains("READY.", m.ScreenText());
    }

    [Fact]
    public void ResetStartsAgainFromTheKernalWithTheDiskStillInTheDrive()
    {
        var m = Boot();
        if (m == null) return;
        var image = D64Image.Create("STAYS", "ST");
        m.MountDisk(image.ToArray());
        m.Type("10 PRINT 1\r");
        m.RunSeconds(0.2);
        m.Reset();
        Assert.True(m.RunUntil(() => m.ScreenText().Contains("READY."), 10));
        Assert.DoesNotContain("10 PRINT 1", m.ScreenText());
        m.Type("LOAD\"$\",8\rLIST\r");
        Assert.True(m.RunUntil(() => m.ScreenText().Contains("STAYS"), 15), m.ScreenText());
    }

    // ---------- a real game with a fast loader (a local, git-ignored image) ----------
    [Fact]
    public void AStateTakenInTheMiddleOfAFastLoadRunsOnLikeTheOriginal()
    {
        var roms = TestRoms.Find();
        if (roms == null) return;
        string? path = TestRoms.FindGame("1943*.g64");
        if (path == null) return;

        var m = new RomMachine(roms);
        m.RunSeconds(3.0);
        m.MountDisk(File.ReadAllBytes(path));
        m.Type("LOAD\"43\",8,1\r");
        Assert.True(m.RunUntil(() => m.Drive!.Cpu.PC is >= 0x0600 and < 0x0700, 20));
        m.RunSeconds(12);                                        // well into the transfer, bytes in flight on the serial bus
        byte[] state = m.SaveState();
        var other = new RomMachine(roms);
        other.LoadState(state);
        m.RunSeconds(8);
        other.RunSeconds(8);                                     // the state was taken where m is now (so both run to the same end)
        Assert.Equal(Signature(m), Signature(other));
        Assert.Equal(m.Bus.Ram, other.Bus.Ram);
    }

    [Fact]
    public void AGameWithAFastLoaderLoadsAndRuns()
    {
        var roms = TestRoms.Find();
        if (roms == null) return;
        string? path = TestRoms.FindGame("1943*.g64");
        if (path == null) return;

        var m = new RomMachine(roms);
        m.RunSeconds(3.0);
        m.MountDisk(File.ReadAllBytes(path));
        m.Type("LOAD\"43\",8,1\r");
        // the boot file uploads a program to the drive's RAM and runs it; the drive then sends the rest over the serial bus with
        // timing that only works if the two processors are in step to within a cycle or two
        Assert.True(m.RunUntil(() => m.Drive!.Cpu.PC is >= 0x0600 and < 0x0700, 20), "the drive runs the uploaded code");
        m.RunSeconds(75);
        Assert.Null(m.HaltReason);
        var frame = new uint[Vic2.FrameWidth * Vic2.FrameHeight];
        m.Bus.Vic.Render(frame);
        int colours = frame.Distinct().Count();
        int background = frame.Count(p => p == frame[0]);
        Assert.True(colours >= 4, $"a picture was drawn ({colours} colours)");
        Assert.True(background < frame.Length * 0.95, "something other than the background is on the screen");
    }
}
