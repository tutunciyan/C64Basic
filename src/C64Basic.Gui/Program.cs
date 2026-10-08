using System.Runtime.InteropServices;
using C64Basic.Core;
using C64Basic.Core.Disk;
using C64Basic.Core.IO;
using C64Basic.Core.Machine;
using C64Basic.Core.Runtime;
using C64Basic.Gui;
using Silk.NET.SDL;

const string Usage = """
    C64Basic GUI - a Commodore 64 BASIC V2 computer in a window

    Usage: C64Basic.Gui [options] [program.bas|program.prg]

      program         load and run it, then stay at the READY. prompt
      --strict        stock BASIC V2 only: no extensions
      --scale <n>     window size as a multiple of 384x272 (default 3)
      --fast          start in warp mode (no C64 speed limit)
      --fullscreen    start full screen
      --type <text>   type this text at startup (\n = RETURN)
      --snapshot <f>  save a screenshot (.bmp) after two seconds and exit
      --disk [n=]<f>  mount a .d64 disk image as device n (default 8; 8-11); a missing file is created blank
      --tape <f>      mount a .t64 tape image as device 1
      -h, --help      show this help

    Keys: Esc = RUN/STOP, F1-F8 = function keys, Ctrl/Alt + 1-8 = colours, numpad = joystick port 2,
          F9 = warp speed, F10 = reset, F11 or Alt+Enter = full screen, F12 = screenshot (.bmp).
          Drop a .d64, .t64, .prg or .bas file on the window to mount or load it.
    """;

bool strict = false, fast = false, fullscreen = false;
int scale = 3;
string? program = null, tape = null, typeText = null, snapshot = null;
var disks = new List<(int Device, string Path)>();

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--strict": strict = true; break;
        case "--fast": fast = true; break;
        case "--fullscreen": fullscreen = true; break;
        case "--scale":
            if (++i >= args.Length || !int.TryParse(args[i], out scale) || scale < 1 || scale > 8)
            { Console.Error.WriteLine("--scale needs a number from 1 to 8"); return 2; }
            break;
        case "--disk":
            {
                if (++i >= args.Length) { Console.Error.WriteLine("--disk needs an image file"); return 2; }
                string spec = args[i];
                int unit = 8, eq = spec.IndexOf('=');
                if (eq > 0 && int.TryParse(spec[..eq], out unit)) spec = spec[(eq + 1)..]; else unit = 8;
                if (unit is < 8 or > 11) { Console.Error.WriteLine("--disk device must be 8-11"); return 2; }
                disks.Add((unit, spec));
                break;
            }
        case "--type":
            if (++i >= args.Length) { Console.Error.WriteLine("--type needs some text"); return 2; }
            typeText = args[i].Replace("\\n", "\n");
            break;
        case "--snapshot":
            if (++i >= args.Length) { Console.Error.WriteLine("--snapshot needs a file name"); return 2; }
            snapshot = args[i];
            break;
        case "--tape":
            if (++i >= args.Length) { Console.Error.WriteLine("--tape needs an image file"); return 2; }
            tape = args[i];
            break;
        case "-h" or "--help": Console.WriteLine(Usage); return 0;
        default:
            if (args[i].StartsWith('-')) { Console.Error.WriteLine($"Unknown option {args[i]}\n\n{Usage}"); return 2; }
            program = args[i];
            break;
    }
}

var screen = new ScreenConsole();
var interpreter = new Interpreter(screen, new HostFileSystem(),
    new InterpreterOptions { Strict = strict, StatementsPerSecond = 1500 }) { Warp = fast };
try
{
    foreach (var (device, path) in disks) interpreter.MountDrive(device, ImageFiles.OpenDisk(path));
    if (tape != null) interpreter.MountDrive(1, ImageFiles.OpenTape(tape));
}
catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
{
    Console.Error.WriteLine(e.Message);
    return 1;
}

var repl = new Repl(interpreter, screen);
var worker = new System.Threading.Thread(() =>
{
    try
    {
        if (program != null && repl.RunFile(program)) repl.Resume();
        else if (program != null) repl.Resume();
        else repl.Run();
    }
    catch (InputEndedException) { }
    finally { screen.Close(); }
})
{ IsBackground = true, Name = "C64" };

if (typeText != null) screen.Inject(typeText);
return SdlHost.Run(interpreter, screen, worker, scale, fullscreen, snapshot);
