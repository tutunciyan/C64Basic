using C64Basic.Console;
using C64Basic.Core;
using C64Basic.Core.Disk;
using C64Basic.Core.IO;
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
      --disk [n=]<f>  mount a .d64 disk image as device n (default 8; 8-11); a missing file is created blank
      --tape <f>      mount a .t64 tape image as device 1 (a missing file is created empty)
      -h, --help      show this help
    """;

bool strict = false, plain = false, fast = false;
int? width = null;
string? file = null;
var disks = new List<(int Device, string Path)>();
string? tape = null;

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--strict": strict = true; break;
        case "--plain": plain = true; break;
        case "--fast": fast = true; break;
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

var device = new ConsoleDevice(emulateScreen: !plain, width ?? 40);
var interpreter = new Interpreter(device, new HostFileSystem(), new InterpreterOptions { Strict = strict, StatementsPerSecond = device.HasScreen && !fast ? 1500 : 0 });
try
{
    foreach (var (dev, path) in disks) interpreter.MountDrive(dev, OpenDisk(path));
    if (tape != null) interpreter.MountDrive(1, OpenTape(tape));
}
catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
{
    device.Restore();
    System.Console.Error.WriteLine(e.Message);
    return 1;
}
var repl = new Repl(interpreter, device);

try
{
    if (file != null) return repl.RunFile(file) ? 0 : 1;
    repl.Run();
    return 0;
}
finally
{
    device.Restore();
}

static D64Image OpenDisk(string path)
{
    Action<byte[]> save = data => File.WriteAllBytes(path, data);
    if (File.Exists(path)) return new D64Image(File.ReadAllBytes(path), save);
    var blank = D64Image.Create(Path.GetFileNameWithoutExtension(path).ToUpperInvariant(), "00", save);
    save(blank.ToArray());
    return blank;
}

static T64Image OpenTape(string path)
{
    Action<byte[]> save = data => File.WriteAllBytes(path, data);
    if (File.Exists(path)) return new T64Image(File.ReadAllBytes(path), save: save);
    var blank = new T64Image(null, Path.GetFileNameWithoutExtension(path).ToUpperInvariant(), save);
    save(blank.ToArray());
    return blank;
}
