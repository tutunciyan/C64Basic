using System.Collections.Concurrent;
using System.Diagnostics;
using C64Basic.Core.IO;
using C64Basic.Core.Disk;
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

    /// <summary>
    /// Whether a mounted G64 file is written back when the drive writes to it (off: a G64 is someone's raw tracks, kept as they were,
    /// and changes live in memory). On, the file is rewritten from the tracks as they are now, speeds included; the first time, the
    /// original is copied to <c>name.g64.bak</c>.
    /// </summary>
    public static bool SaveG64Changes { get; set; }

    public Bus Bus { get; }
    public Cpu6502 Cpu { get; }
    public InputState Input { get; } = new();
    public RomSet Roms { get; }
    /// <summary>Device 8, the first drive (null for a machine without drives).</summary>
    public Drive1541? Drive => Drives.Length > 0 ? Drives[0] : null;

    /// <summary>The drives on the serial bus: device 8, and device 9 when the machine was made with two.</summary>
    public Drive1541[] Drives { get; }

    /// <summary>The cassette recorder on the cassette port.</summary>
    public Datasette Tape { get; } = new();

    public IecBus? Iec { get; }

    readonly Lockstep _lockstep;
    readonly ConcurrentQueue<Action> _posted = new();
    readonly Queue<char> _typed = new();

    /// <summary>
    /// Raised (on the machine's thread) after the drive has written to the disk and gone back to reading, with the disk as it is now. A
    /// host saves the image from here.
    /// </summary>
    public event Action<GcrDisk>? DiskWritten;

    /// <summary>While true <see cref="RunPaced"/> lets the machine stand still (the host looks at <see cref="Registers"/>); time does not run on.</summary>
    public volatile bool Paused;

    /// <summary>Why the machine stopped by itself (an opcode that is not implemented), or null.</summary>
    public string? HaltReason { get; private set; }

    public bool Halted => HaltReason != null;

    public RomMachine(RomSet roms, bool withDrive = true, int driveCount = 1)
    {
        if (driveCount is < 1 or > 2) throw new ArgumentOutOfRangeException(nameof(driveCount), "one or two drives");
        Roms = roms;
        Bus = new Bus();
        // the clock of every chip is the processor's own cycle count, so a run is the same every time
        Bus.Seconds = () => ((Cpu?.AccessCycle ?? 0) + 0.5) / ClockHz;
        Bus.EnableRomMode(roms.Basic, roms.Kernal);
        if (roms.Chargen != null) Bus.LoadCharacterRom(roms.Chargen);
        Bus.Input = Input;

        Cpu = new Cpu6502((ICpuMemory)Bus) { IrqLine = () => Bus.IrqLine, NmiLine = () => Bus.NmiLine, Stall = Bus.Vic.StallCycles };
        var c64 = new C64Member(this);
        var members = new List<ILockstepMember> { c64 };
        if (withDrive)
        {
            Drives = new Drive1541[driveCount];
            _slots = new DiskSlot[driveCount];
            Iec = new IecBus(Bus.Cia2) { RiseDelay = IecRiseSeconds };
            for (int i = 0; i < driveCount; i++)
            {
                Drives[i] = new Drive1541(roms.Dos);
                _slots[i] = new DiskSlot();
                Iec.Attach(Drives[i], 8 + i);
                members.Add(new DriveMember(this, i));
            }
        }
        else { Drives = Array.Empty<Drive1541>(); _slots = Array.Empty<DiskSlot>(); }
        _lockstep = new Lockstep(members.ToArray());
        Reset();
    }

    /// <summary>Power on: RAM and chips cleared, the processor starts at the KERNAL's reset vector. A mounted disk stays in the drive.</summary>
    public void Reset()
    {
        HaltReason = null;
        Bus.PowerOn();
        Cpu.Reset();
        _typed.Clear();
        foreach (var drive in Drives) drive.Reset();
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
            var tape = _m.Tape;
            if (tape.Active) tape.Update(cpu.Cycles, _m.Bus.CassetteMotor, _m.Bus.CassetteWrite, _m.Bus.Cia1.PulseFlag);
            cpu.StepNative();
            if (cpu.StopReason != CpuStop.None)
            {
                _m.HaltReason = $"{cpu.StopReason} at ${cpu.PC:X4} (opcode ${_m.Bus.Read(cpu.PC):X2})";
                return;
            }
        }
    }

    sealed class DriveMember : ILockstepMember
    {
        readonly RomMachine _m;
        readonly int _index;
        public DriveMember(RomMachine m, int index) { _m = m; _index = index; }
        Drive1541 Drive => _m.Drives[_index];
        public long Cycles => Drive.Cycles;
        public double Hz => Drive1541.ClockHz;
        public double NextAccessTime => Drive.Cpu.NextAccessCycle / Hz;

        public void Step()
        {
            var drive = Drive;
            drive.Step();
            if (drive.Mechanics.TakeWrites() && drive.Mechanics.Disk is { } disk) _m.DriveWrote(_index, disk);
            if (drive.Cpu.StopReason != CpuStop.None)
                _m.HaltReason = $"1541 (device {8 + _index}): {drive.Cpu.StopReason} at ${drive.Cpu.PC:X4} (opcode ${drive.Cpu.PeekOpcode():X2})";
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
            if (Halted || Paused) { Thread.Sleep(20); origin = Seconds - clock.Elapsed.TotalSeconds; continue; }
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

    // ---------- saved state ----------
    const string StateMagic = "C64ROMSTATE";
    const int StateVersion = 4;                  // 2: the drive head's position inside a bit and its direction; 3: the number of drives; 4: the datasette

    /// <summary>
    /// The whole machine as bytes: the processor, RAM and every chip of the C64, and the drive with its processor, RAM, VIAs, head
    /// position and the disk as it is now (the tracks, so a game that wrote to its disk keeps that). Call it from the machine's own
    /// thread (<see cref="Post"/>) while it runs.
    /// </summary>
    public byte[] SaveState()
    {
        using var stream = new MemoryStream();
        using (var w = new BinaryWriter(stream))
        {
            w.Write(StateMagic);
            w.Write(StateVersion);
            w.Write(Drives.Length > 0);
            w.Write(Drives.Length);
            Cpu.SaveState(w);
            Bus.SaveState(w);
            if (Drives.Length > 0)
            {
                foreach (var drive in Drives) drive.SaveState(w);
                Iec!.SaveState(w);
            }
            Tape.SaveState(w);
        }
        return stream.ToArray();
    }

    /// <summary>Restores what <see cref="SaveState"/> wrote. Throws <see cref="InvalidDataException"/> for something that is not a state of this kind of machine.</summary>
    public void LoadState(byte[] data)
    {
        using var r = new BinaryReader(new MemoryStream(data));
        string magic;
        try { magic = r.ReadString(); }
        catch (Exception e) when (e is EndOfStreamException or IOException or ArgumentException) { throw new InvalidDataException("not a ROM mode state file"); }
        if (magic != StateMagic) throw new InvalidDataException("not a ROM mode state file");
        int version = r.ReadInt32();
        if (version is < 1 or > StateVersion) throw new InvalidDataException($"state file version {version} is not supported");
        bool hadDrive = r.ReadBoolean();
        int hadDrives = version >= 3 ? r.ReadInt32() : hadDrive ? 1 : 0;
        if (hadDrives != Drives.Length && hadDrive && Drives.Length > 0) throw new InvalidDataException($"the state has {hadDrives} drive(s) and this machine has {Drives.Length}");
        if (hadDrive != (Drives.Length > 0)) throw new InvalidDataException(hadDrive ? "the state has a drive and this machine has none" : "this machine has a drive and the state has none");

        HaltReason = null;
        Cpu.LoadState(r);          // first: the chips take their time from the processor's clock
        Bus.LoadState(r);
        if (Drives.Length > 0)
        {
            foreach (var drive in Drives) drive.LoadState(r, version);
            Iec!.LoadState(r);
        }
        if (version >= 4) Tape.LoadState(r);
        lock (_typed) _typed.Clear();
    }

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

    /// <summary>The processors in a line of text: C64 PC, A, X, Y, SP and the flags, then each 1541's PC.</summary>
    public string Registers()
    {
        string Flags(Cpu6502 c) => new string(new[]
        {
            c.GetFlag(Cpu6502.FlagN) ? 'N' : 'n', c.GetFlag(Cpu6502.FlagV) ? 'V' : 'v', '-', c.GetFlag(Cpu6502.FlagB) ? 'B' : 'b',
            c.GetFlag(Cpu6502.FlagD) ? 'D' : 'd', c.GetFlag(Cpu6502.FlagI) ? 'I' : 'i', c.GetFlag(Cpu6502.FlagZ) ? 'Z' : 'z', c.GetFlag(Cpu6502.FlagC) ? 'C' : 'c',
        });
        var sb = new System.Text.StringBuilder();
        sb.Append($"PC={Cpu.PC:X4} A={Cpu.A:X2} X={Cpu.X:X2} Y={Cpu.Y:X2} SP={Cpu.SP:X2} {Flags(Cpu)} line {Bus.Vic.Raster}");
        for (int i = 0; i < Drives.Length; i++) sb.Append($" | 1541 #{8 + i} PC={Drives[i].Cpu.PC:X4} track {Drives[i].Mechanics.Track:0.#}");
        return sb.ToString();
    }

    /// <summary>
    /// The next (<paramref name="step"/> = 1) or previous (-1) disk image, in name order, in the folder of <paramref name="current"/>:
    /// how a multi-disk game's disks sit side by side. Null when there is no other image there.
    /// </summary>
    public static string? SiblingImage(string current, int step)
    {
        string? folder = Path.GetDirectoryName(Path.GetFullPath(current));
        if (folder == null || !Directory.Exists(folder)) return null;
        var images = Directory.GetFiles(folder)
            .Where(f => Path.GetExtension(f).ToLowerInvariant() is ".d64" or ".g64")
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
        int at = images.FindIndex(f => string.Equals(f, Path.GetFullPath(current), StringComparison.OrdinalIgnoreCase));
        if (images.Count < 2 || at < 0) return null;
        return images[((at + step) % images.Count + images.Count) % images.Count];
    }

    /// <summary>Swaps the disk of a drive for the next or previous image in its folder; returns the new path, or null if there is none.</summary>
    public string? SwapDisk(int step, int device = 8)
    {
        string? current = Slot(device).Path;
        if (current == null) return null;
        string? next = SiblingImage(current, step);
        if (next != null) MountDiskFile(next, device);
        return next;
    }

    // ---------- the tape ----------
    /// <summary>The file of the tape image in the recorder, or null.</summary>
    public string? TapePath { get; private set; }

    /// <summary>
    /// Puts a tape in the recorder with PLAY pressed (so <c>LOAD</c> and <c>SAVE</c> to device 1 find the sense switch closed and the
    /// real KERNAL routines read and write its pulses). Pulses recorded by a <c>SAVE</c> are added to the tape when the motor stops.
    /// </summary>
    public void MountTape(TapImage tape)
    {
        TapePath = null;
        Tape.Insert(tape);
        Bus.CassettePlay = true;
    }

    /// <summary>
    /// Puts a <c>.tap</c> (a missing one is created empty and written as pulses are recorded) or <c>.t64</c> (its programs are put on a
    /// tape in the KERNAL's format, in memory only) in the recorder.
    /// </summary>
    public void MountTapeFile(string path)
    {
        string extension = Path.GetExtension(path).ToLowerInvariant();
        TapImage tape;
        if (extension == ".t64")
        {
            tape = new TapImage(null, Path.GetFileNameWithoutExtension(path).ToUpperInvariant());
            var t64 = new T64Image(File.ReadAllBytes(path));
            foreach (var entry in t64.Directory())
                tape.Write(Path.GetFileNameWithoutExtension(entry.Name).ToUpperInvariant(), FileType.Prg, t64.Read(entry.Name).Data, replace: true);
        }
        else
        {
            bool exists = File.Exists(path);
            tape = new TapImage(exists ? File.ReadAllBytes(path) : null, Path.GetFileNameWithoutExtension(path).ToUpperInvariant(),
                bytes => SaveTapeFile(path, bytes));
            if (!exists) SaveTapeFile(path, tape.ToArray());
        }
        MountTape(tape);
        if (extension != ".t64") TapePath = path;
    }

    public void EjectTape()
    {
        Tape.Insert(null);
        Bus.CassettePlay = false;
        TapePath = null;
    }

    /// <summary>Lets go of PLAY: the sense switch opens (the KERNAL asks for it again) and the tape stands still.</summary>
    public void StopTape()
    {
        Tape.Play = false;
        Bus.CassettePlay = false;
    }

    /// <summary>Presses PLAY again.</summary>
    public void PlayTape()
    {
        if (!Tape.Active) return;
        Tape.Play = true;
        Bus.CassettePlay = true;
    }

    void SaveTapeFile(string path, byte[] bytes)
    {
        try
        {
            string temp = path + ".tmp";
            File.WriteAllBytes(temp, bytes);
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            SaveError = e.Message;
        }
    }

    // ---------- the drives ----------
    sealed class DiskSlot
    {
        public string? Path;
        public bool SavesChanges;
    }

    readonly DiskSlot[] _slots;

    DiskSlot Slot(int device) =>
        device - 8 is >= 0 and < 2 && device - 8 < _slots.Length ? _slots[device - 8] : throw new ArgumentOutOfRangeException(nameof(device), $"there is no drive with device number {device}");

    /// <summary>The file of the disk image mounted in a drive (device 8 or 9), or null for one that is only in memory.</summary>
    public string? DiskPathOf(int device) => Slot(device).Path;

    /// <summary>True when changes the drive writes are saved back to <see cref="DiskPathOf"/>: a D64 is, a G64 (raw tracks of someone else's disk) is only with <see cref="SaveG64Changes"/>.</summary>
    public bool DiskSavesChangesOf(int device) => Slot(device).SavesChanges;

    /// <summary>The file of the disk image mounted in device 8, or null for one that is only in memory.</summary>
    public string? DiskPath => _slots.Length > 0 ? _slots[0].Path : null;

    /// <summary>True when changes the drive writes to device 8 are saved back to <see cref="DiskPath"/> (see <see cref="DiskSavesChangesOf"/>).</summary>
    public bool DiskSavesChanges => _slots.Length > 0 && _slots[0].SavesChanges;

    /// <summary>Puts a D64 or G64 image in a drive (device 8 or 9). It lives in memory only.</summary>
    public void MountDisk(byte[] image, int device = 8)
    {
        var slot = Slot(device);
        slot.Path = null;
        slot.SavesChanges = false;
        Drives[device - 8].InsertDisk(GcrDisk.FromImage(image));
    }

    /// <summary>
    /// Puts the disk image in a file in a drive (device 8 or 9). A D64 is saved back whenever the drive has written to it (a missing one
    /// is created blank); a G64 is read only unless <see cref="SaveG64Changes"/> is on, changes stay in memory.
    /// </summary>
    public void MountDiskFile(string path, int device = 8)
    {
        var slot = Slot(device);
        bool g64 = path.EndsWith(".g64", StringComparison.OrdinalIgnoreCase);
        string extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension is ".t64" or ".prg")
        {
            // a tape image or a single program: its files go on a blank disk (in memory only), to LOAD"NAME",8,1
            MountDisk(DiskFromPrograms(path, extension), device);
            slot.Path = path;
            return;
        }
        byte[] image;
        if (File.Exists(path)) image = File.ReadAllBytes(path);
        else if (!g64)
        {
            image = D64Image.Create(Path.GetFileNameWithoutExtension(path).ToUpperInvariant(), "00").ToArray();
            File.WriteAllBytes(path, image);
        }
        else throw new FileNotFoundException("disk image not found: " + path, path);
        MountDisk(image, device);
        slot.Path = path;
        slot.SavesChanges = !g64 || SaveG64Changes;
    }

    static byte[] DiskFromPrograms(string path, string extension)
    {
        var disk = D64Image.Create(Path.GetFileNameWithoutExtension(path).ToUpperInvariant(), "00");
        byte[] data = File.ReadAllBytes(path);
        if (extension == ".prg")
            disk.Write(Path.GetFileNameWithoutExtension(path).ToUpperInvariant(), FileType.Prg, data, replace: false);
        else
        {
            var tape = new T64Image(data);
            foreach (var entry in tape.Directory())
                disk.Write(Path.GetFileNameWithoutExtension(entry.Name).ToUpperInvariant(), FileType.Prg, tape.Read(entry.Name).Data, replace: true);
        }
        return disk.ToArray();
    }

    public void EjectDisk(int device = 8)
    {
        var slot = Slot(device);
        slot.Path = null;
        slot.SavesChanges = false;
        Drives[device - 8].InsertDisk(null);
    }

    void DriveWrote(int index, GcrDisk disk)
    {
        SaveDiskFile(_slots[index], disk);
        DiskWritten?.Invoke(disk);
    }

    /// <summary>
    /// Writes the disk back to its file: a D64 once the sectors on it are all readable again (not halfway through a format), a G64
    /// as the tracks are, after a copy of the original.
    /// </summary>
    void SaveDiskFile(DiskSlot slot, GcrDisk disk)
    {
        if (slot.Path == null || !slot.SavesChanges) return;
        string path = slot.Path;
        bool g64 = path.EndsWith(".g64", StringComparison.OrdinalIgnoreCase);
        byte[] bytes;
        if (g64) bytes = disk.ToG64();
        else
        {
            var (d64, report) = disk.ToD64();
            if (!report.Clean) return;
            bytes = d64;
        }
        string temp = path + ".tmp";
        try
        {
            if (g64 && !File.Exists(path + ".bak") && File.Exists(path)) File.Copy(path, path + ".bak");
            File.WriteAllBytes(temp, bytes);
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            SaveError = e.Message;
        }
    }

    /// <summary>The message of the last failure to save a disk file, or null.</summary>
    public string? SaveError { get; private set; }
}
