using C64Basic.Console;
using C64Basic.Core;
using C64Basic.Core.Disk;
using C64Basic.Core.IO;
using C64Basic.Core.Rom;
using C64Basic.Core.Runtime;

const string Usage = """
    C64Basic - a Commodore 64 BASIC V2 interpreter

    Usage: C64Basic [options] [program.bas]

      program.bas     load and run a program, then exit
      --run <file>    same as above
      --strict        stock BASIC V2 only: no extensions, 80-character lines
      --plain         plain text output: no emulated C64 screen, colours or border
      --width <n>     emulated screen columns, centred with a border (default 40, like a real C64);
                      0 = use the whole terminal width
      --fast          run at full speed instead of C64 speed
      --pixels        draw the real VIC-II picture (sprites, graphics modes) with half blocks instead of the text-only screen;
                      Esc = RUN/STOP, Ctrl+D quits; no sound (use the GUI)
      --disk [n=]<f>  mount a .d64 (or read-only .g64) disk image as device n (default 8; 8-11; ROM mode has drives 8 and 9); a missing file is created blank
      --tape <f>      mount a .t64 or .tap tape image as device 1 (a missing file is created empty); in ROM mode a datasette with PLAY pressed
      --iec-rise <us> ROM mode: microseconds a released serial line takes to go high (default 1.2; fast loaders need about 0.8 to 1.7)
      --cart <f>      ROM mode: plug in a .crt cartridge (normal 8K/16K/Ultimax, Ocean, C64 Game System, Magic Desk, EasyFlash read-only)
      --reu <kb>      ROM mode: plug in a RAM expansion unit of 128, 256, 512 ... 16384 KB (a cartridge and an REU share $DF00: the cartridge wins)
      --write-g64     ROM mode: save what the drive writes to a mounted .g64 back into the file (the first time the original is copied to .g64.bak)
      --rom-dir <d>   ROM mode: run the real C64 BASIC and KERNAL ROMs and a real 1541 instead of the built-in BASIC (needs the ROM
                      dumps in <d>; draws the picture like --pixels, so it needs a terminal; --disk mounts a .d64 or .g64, --fast
                      skips the C64 speed limit; no program file, --plain or --strict)
      -h, --help      show this help
    """;

bool strict = false, plain = false, fast = false, pixels = false;
int? width = null;
string? file = null, romDir = null;
var disks = new List<(int Device, string Path)>();
string? tape = null, cart = null;
int reuKb = 0;

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--strict": strict = true; break;
        case "--plain": plain = true; break;
        case "--fast": fast = true; break;
        case "--pixels": pixels = true; break;
        case "--width":
            if (++i >= args.Length || !int.TryParse(args[i], out int w) || (w != 0 && w < 40))
            { System.Console.Error.WriteLine("--width needs 0 (terminal width) or a number of at least 40"); return 2; }
            width = w;
            break;
        case "--disk":
            {
                if (++i >= args.Length) { System.Console.Error.WriteLine("--disk needs an image file"); return 2; }
                string spec = args[i];
                int unit = 8, eq = spec.IndexOf('=');
                if (eq > 0 && int.TryParse(spec[..eq], out unit)) spec = spec[(eq + 1)..];
                else unit = 8;
                if (unit is < 8 or > 11) { System.Console.Error.WriteLine("--disk device must be 8-11"); return 2; }
                disks.Add((unit, spec));
                break;
            }
        case "--iec-rise":
            if (++i >= args.Length || !double.TryParse(args[i], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double rise) || rise < 0 || rise > 10)
            { System.Console.Error.WriteLine("--iec-rise needs a number of microseconds from 0 to 10"); return 2; }
            RomMachine.IecRiseSeconds = rise * 1e-6;
            break;
        case "--cart":
            if (++i >= args.Length) { System.Console.Error.WriteLine("--cart needs a .crt file"); return 2; }
            cart = args[i];
            break;
        case "--reu":
            if (++i >= args.Length || !int.TryParse(args[i], out reuKb) || reuKb < 128 || reuKb > 16384 || (reuKb & (reuKb - 1)) != 0)
            { System.Console.Error.WriteLine("--reu needs a size in KB: 128, 256, 512, 1024 ... 16384"); return 2; }
            break;
        case "--write-g64":
            RomMachine.SaveG64Changes = true;
            break;
        case "--rom-dir":
            if (++i >= args.Length) { System.Console.Error.WriteLine("--rom-dir needs a folder with the ROM dumps"); return 2; }
            romDir = args[i];
            break;
        case "--tape":
            if (++i >= args.Length) { System.Console.Error.WriteLine("--tape needs an image file"); return 2; }
            tape = args[i];
            break;
        case "-h" or "--help": System.Console.WriteLine(Usage); return 0;
        case "--run":
            if (++i >= args.Length) { System.Console.Error.WriteLine("--run needs a file name"); return 2; }
            file = args[i];
            break;
        default:
            if (args[i].StartsWith('-')) { System.Console.Error.WriteLine($"Unknown option {args[i]}\n\n{Usage}"); return 2; }
            file = args[i];
            break;
    }
}

if (pixels && !PixelTerminal.Available)
{
    System.Console.Error.WriteLine("--pixels needs a terminal (input and output must not be redirected)");
    return 2;
}

if (romDir != null)
{
    if (!PixelTerminal.Available) { System.Console.Error.WriteLine("--rom-dir needs a terminal (input and output must not be redirected)"); return 2; }
    if (file != null || strict || plain || width != null)
    { System.Console.Error.WriteLine("--rom-dir runs the real ROMs: it takes no program file, --plain, --width or --strict (use --disk with a disk image)"); return 2; }
    if (disks.Any(d => d.Device is not (8 or 9)) || disks.Select(d => d.Device).Distinct().Count() != disks.Count)
    { System.Console.Error.WriteLine("ROM mode has up to two drives, devices 8 and 9: one --disk for each"); return 2; }
    RomMachine machine;
    try
    {
        var roms = RomSet.Find(romDir);
        foreach (string note in roms.Notes) System.Console.Error.WriteLine("ROM mode: " + note);
        machine = new RomMachine(roms, driveCount: disks.Any(d => d.Device == 9) ? 2 : 1);
        foreach (var disk in disks) machine.MountDiskFile(disk.Path, disk.Device);
        if (tape != null) machine.MountTapeFile(tape);
        if (reuKb != 0) machine.InsertReu(reuKb);
        if (cart != null) machine.InsertCartridge(C64Basic.Core.Machine.Cartridge.FromCrt(File.ReadAllBytes(cart)));
    }
    catch (Exception e) when (e is IOException or InvalidDataException or ArgumentException or UnauthorizedAccessException)
    {
        System.Console.Error.WriteLine(e.Message);
        return 1;
    }
    var source = new RomPixelSource(machine);
    var romView = new PixelTerminal(source);
    var machineThread = new Thread(() => machine.RunPaced(() => source.Quitting.IsSet, () => fast)) { IsBackground = true, Name = "C64 ROM mode" };
    try
    {
        romView.Start();
        machineThread.Start();
        source.Quitting.Wait();
        machineThread.Join(1000);
        return 0;
    }
    finally { romView.Stop(); }
}

ScreenConsole? pixelConsole = pixels ? new ScreenConsole() : null;
ConsoleDevice? device = pixels ? null : new ConsoleDevice(emulateScreen: !plain, width ?? 40);
IConsoleDevice console = pixelConsole != null ? pixelConsole : plain ? new ShadowScreenConsole(device!) : device!;
bool paced = pixels || device!.HasScreen;
var interpreter = new Interpreter(console, new HostFileSystem(), new InterpreterOptions { Strict = strict, StatementsPerSecond = paced && !fast ? 1500 : 0 });
try
{
    foreach (var (dev, path) in disks) interpreter.MountDrive(dev, OpenDisk(path));
    if (tape != null) interpreter.MountDrive(1, OpenTape(tape));
}
catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
{
    device?.Restore();
    System.Console.Error.WriteLine(e.Message);
    return 1;
}
var repl = new Repl(interpreter, console);

PixelTerminal? view = pixelConsole != null ? new PixelTerminal(new InterpreterPixelSource(interpreter, pixelConsole)) : null;
try
{
    view?.Start();
    if (file != null)
    {
        bool ok = repl.RunFile(file);
        if (!pixels) return ok ? 0 : 1;
        repl.Resume();   // the picture stays up: carry on at the prompt, like the GUI
        return ok ? 0 : 1;
    }
    repl.Run();
    return 0;
}
finally
{
    view?.Stop();
    device?.Restore();
}

static D64Image OpenDisk(string path)
{
    Action<byte[]> save = data => File.WriteAllBytes(path, data);
    if (File.Exists(path) && path.EndsWith(".g64", StringComparison.OrdinalIgnoreCase))
    {
        // raw GCR tracks, read as the sectors they hold; changes stay in memory and the file is never written back
        var (disk, report) = G64Image.Decode(File.ReadAllBytes(path));
        System.Console.Error.WriteLine($"{Path.GetFileName(path)}: {report}");
        return new D64Image(disk);
    }
    if (File.Exists(path)) return new D64Image(File.ReadAllBytes(path), save);
    var blank = D64Image.Create(Path.GetFileNameWithoutExtension(path).ToUpperInvariant(), "00", save);
    save(blank.ToArray());
    return blank;
}

static IDiskDrive OpenTape(string path)
{
    Action<byte[]> save = data => File.WriteAllBytes(path, data);
    string title = Path.GetFileNameWithoutExtension(path).ToUpperInvariant();
    bool pulses = path.EndsWith(".tap", StringComparison.OrdinalIgnoreCase);
    bool exists = File.Exists(path);
    IDiskDrive tape = pulses
        ? new TapImage(exists ? File.ReadAllBytes(path) : null, title, save)
        : new T64Image(exists ? File.ReadAllBytes(path) : null, title, save);
    if (!exists) save(tape is TapImage t ? t.ToArray() : ((T64Image)tape).ToArray());
    return tape;
}
