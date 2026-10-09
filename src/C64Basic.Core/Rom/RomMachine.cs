using System.Collections.Concurrent;
using System.Diagnostics;
using C64Basic.Core.IO;
using C64Basic.Core.Machine;
using C64Basic.Core.Runtime;

namespace C64Basic.Core.Rom;

/// <summary>
/// A whole Commodore 64 running its own ROMs: the real KERNAL and BASIC on the 6502 (no emulated routines, the real interrupt
/// handler, keyboard scan, serial bus routines), with a real 1541 beside it, a 6502 running the DOS ROM on a disk image, joined
/// by the serial bus. The two processors run in lockstep, so a fast loader that uploads its own code to the drive and then races the
/// bus works. The BASIC interpreter written for this project is not involved; this is the other, slower and exact way to run.
/// </summary>
public sealed class RomMachine
{
    public const double ClockHz = Cia.ClockHz;

    /// <summary>How long a released serial line takes to go high (see <see cref="IecBus.RiseDelay"/>).</summary>
    public static double IecRiseSeconds { get; set; } = 1.2e-6;

    public Bus Bus { get; }
    public Cpu6502 Cpu { get; }
    public InputState Input { get; } = new();
    public RomSet Roms { get; }
    public Drive1541? Drive { get; }
    public IecBus? Iec { get; }

    readonly Lockstep _lockstep;
    readonly ConcurrentQueue<Action> _posted = new();
    readonly Queue<char> _typed = new();

    /// <summary>Why the machine stopped by itself (an opcode that is not implemented), or null.</summary>
    public string? HaltReason { get; private set; }

    public bool Halted => HaltReason != null;

    public RomMachine(RomSet roms, bool withDrive = true)
    {
        Roms = roms;
        Bus = new Bus();
        // the clock of every chip is the processor's own cycle count, so a run is the same every time
        Bus.Seconds = () => ((Cpu?.AccessCycle ?? 0) + 0.5) / ClockHz;
        Bus.EnableRomMode(roms.Basic, roms.Kernal);
        if (roms.Chargen != null) Bus.LoadCharacterRom(roms.Chargen);
        Bus.Input = Input;

        Cpu = new Cpu6502((ICpuMemory)Bus) { IrqLine = () => Bus.IrqLine, NmiLine = () => Bus.NmiLine };
        var c64 = new C64Member(this);
        if (withDrive)
        {
            Drive = new Drive1541(roms.Dos);
            Iec = new IecBus(Bus.Cia2);
            Iec.Attach(Drive, 8);
            Iec.RiseDelay = IecRiseSeconds;
            _lockstep = new Lockstep(c64, new DriveMember(this));
        }
        else _lockstep = new Lockstep(c64);
        Reset();
    }

    /// <summary>Power on: RAM and chips cleared, the processor starts at the KERNAL's reset vector. A mounted disk stays in the drive.</summary>
    public void Reset()
    {
        HaltReason = null;
        Bus.PowerOn();
        Cpu.Reset();
        _typed.Clear();
        Drive?.Reset();
    }

    // ---------- time ----------
    public long Cycles => Cpu.Cycles;

    /// <summary>Seconds of emulated time since power-on.</summary>
    public double Seconds => Cpu.Cycles / ClockHz;

    sealed class C64Member : ILockstepMember
    {
        readonly RomMachine _m;
        public C64Member(RomMachine m) => _m = m;
        public long Cycles => _m.Cpu.Cycles;
        public double Hz => ClockHz;
        public double NextAccessTime => _m.Cpu.NextAccessCycle / Hz;

        public void Step()
        {
            var cpu = _m.Cpu;
            long before = cpu.Cycles;
            cpu.StepNative();
            if (cpu.StopReason != CpuStop.None)
            {
                _m.HaltReason = $"{cpu.StopReason} at ${cpu.PC:X4} (opcode ${_m.Bus.Read(cpu.PC):X2})";
                return;
            }
            cpu.Cycles += _m.Bus.Vic.StolenCycles(_m.Bus.AbsoluteCycleOf(before), _m.Bus.AbsoluteCycleOf(cpu.Cycles));
        }
    }

    sealed class DriveMember : ILockstepMember
    {
        readonly RomMachine _m;
        public DriveMember(RomMachine m) => _m = m;
        public long Cycles => _m.Drive!.Cycles;
        public double Hz => Drive1541.ClockHz;
        public double NextAccessTime => _m.Drive!.Cpu.NextAccessCycle / Hz;

        public void Step()
        {
            var drive = _m.Drive!;
            drive.Step();
            if (drive.Cpu.StopReason != CpuStop.None)
                _m.HaltReason = $"1541: {drive.Cpu.StopReason} at ${drive.Cpu.PC:X4} (opcode ${drive.Cpu.PeekOpcode():X2})";
        }
    }

    /// <summary>Runs for a number of emulated seconds (at full speed, no pacing), feeding typed text to the keyboard buffer.</summary>
    public void RunSeconds(double seconds)
    {
        double end = Seconds + seconds;
        while (Seconds < end && !Halted)
        {
            Feed();
            if (_lockstep.RunUntil(Math.Min(end, Seconds + 0.005), () => Halted) == 0) break;   // within an instruction of the end
        }
    }

    /// <summary>Runs until the condition holds, checking every 5 ms of emulated time; false if it did not within the time or the machine halted.</summary>
    public bool RunUntil(Func<bool> condition, double timeoutSeconds)
    {
        double end = Seconds + timeoutSeconds;
        while (!Halted)
        {
            if (condition()) return true;
            if (Seconds >= end) return false;
            Feed();
            _lockstep.RunUntil(Seconds + 0.005, () => Halted);
        }
        return condition();
    }

    /// <summary>
    /// Runs forever (until <paramref name="shouldStop"/>) at the speed of the real machine, or flat out while <paramref name="warp"/>
    /// returns true. Work posted with <see cref="Post"/> runs here, between slices of 4 ms.
    /// </summary>
    public void RunPaced(Func<bool> shouldStop, Func<bool> warp)
    {
        var clock = Stopwatch.StartNew();
        double origin = Seconds;
        while (!shouldStop())
        {
            while (_posted.TryDequeue(out var action)) action();
            if (Halted) { Thread.Sleep(20); continue; }
            Feed();
            _lockstep.RunUntil(Seconds + 0.004, () => Halted);
            double ahead = Seconds - origin - clock.Elapsed.TotalSeconds;
            if (warp()) origin = Seconds - clock.Elapsed.TotalSeconds;       // no debt to pay off when warp ends
            else if (ahead > 0.002) Thread.Sleep((int)(ahead * 1000));
            else if (ahead < -0.25) origin = Seconds - clock.Elapsed.TotalSeconds;   // far behind (the host was busy): do not run to catch up
        }
    }

    /// <summary>Has an action run on the machine's own thread, between slices (mounting a disk, resetting, saving state).</summary>
    public void Post(Action action) => _posted.Enqueue(action);

    // ---------- typing ----------
    /// <summary>
    /// Types text as if from the keyboard, through the KERNAL's own keyboard buffer (ten characters, drained by the editor):
    /// characters wait in a queue until there is room. '\n' is RETURN.
    /// </summary>
    public void Type(string text)
    {
        lock (_typed) foreach (char c in text) _typed.Enqueue(c == '\n' ? '\r' : c);
    }

    void Feed()
    {
        if (Bus.Ram[0x289] != 10) return;                    // the KERNAL has not set up its keyboard buffer yet (it would wipe the text)
        lock (_typed)
        {
            while (_typed.Count > 0 && Bus.Ram[0xC6] < 10)
            {
                int count = Bus.Ram[0xC6];
                Bus.Ram[0x277 + count] = (byte)Petscii.ToCode(_typed.Dequeue());
                Bus.Ram[0xC6] = (byte)(count + 1);
            }
        }
    }

    /// <summary>True while typed text is still waiting for room in the keyboard buffer.</summary>
    public bool Typing
    {
        get { lock (_typed) return _typed.Count > 0; }
    }

    // ---------- the screen ----------
    public const int Columns = 40, Rows = 25;

    /// <summary>The text on the screen, one line per row, trailing spaces and empty rows at the end removed.</summary>
    public string ScreenText()
    {
        int baseAddress = Bus.Ram[0x288] << 8;               // the KERNAL's pointer to the screen memory page
        if (baseAddress == 0) baseAddress = 0x400;
        var rows = new List<string>();
        for (int r = 0; r < Rows; r++)
        {
            var sb = new System.Text.StringBuilder(Columns);
            for (int c = 0; c < Columns; c++) sb.Append(Petscii.ScreenGlyph(Bus.Ram[baseAddress + r * Columns + c]));
            rows.Add(sb.ToString().TrimEnd(' '));
        }
        while (rows.Count > 0 && rows[^1].Length == 0) rows.RemoveAt(rows.Count - 1);
        return string.Join("\n", rows);
    }

    // ---------- the drive ----------
    /// <summary>Puts a D64 or G64 image in the drive.</summary>
    public void MountDisk(byte[] image) => Drive?.InsertDisk(GcrDisk.FromImage(image));

    public void EjectDisk() => Drive?.InsertDisk(null);
}
