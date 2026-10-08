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
      --tape <f>      mount a .t64 or .tap tape image as device 1
      --sid <6581|8580>  sound chip model (default 6581: darker filter, 8580: cleaner and linear)
      --lightpen      the mouse is a light pen on port 1 (hold the left button over the picture) instead of a paddle
      --joy <1|2>     joystick port the numpad drives (default 2; the Pause key switches); game controllers use port 2, then 1
      --state <f>     file for Ctrl+S (save machine state) and Ctrl+L (load); default c64-state.sav
      --resume        load the state file at startup
      --chargen <f>   use a 4096-byte character ROM dump instead of the built-in character set
      -h, --help      show this help

    Keys: Esc = RUN/STOP, Shift+Alt = switch character set (Alt is the Commodore key), Shift+letter = graphics like a real C64, F1-F8 = function keys, Ctrl/Alt + 1-8 = colours, numpad = joystick port 2,
          F9 = warp speed, F10 = reset, F11 or Alt+Enter = full screen, F12 = screenshot (.bmp).
          Drop a .d64, .t64, .tap, .prg, .bas or .sav file on the window to mount or load it.
    """;

bool strict = false, fast = false, fullscreen = false;
int scale = 3;
int joyPort = 2;
var sidModel = Sid.SidModel.Mos6581;
string? stateFile = null;
bool resume = false;
string? program = null, tape = null, typeText = null, snapshot = null, chargen = null;
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
        case "--joy":
            if (++i >= args.Length || !int.TryParse(args[i], out joyPort) || joyPort is < 1 or > 2)
            { Console.Error.WriteLine("--joy needs 1 or 2"); return 2; }
            break;
        case "--lightpen": SdlHost.LightPen = true; break;
        case "--sid":
            if (++i >= args.Length || args[i] is not ("6581" or "8580"))
            { Console.Error.WriteLine("--sid needs 6581 or 8580"); return 2; }
            sidModel = args[i] == "8580" ? Sid.SidModel.Mos8580 : Sid.SidModel.Mos6581;
            break;
        case "--state":
            if (++i >= args.Length) { Console.Error.WriteLine("--state needs a file name"); return 2; }
            stateFile = args[i];
            break;
        case "--resume": resume = true; break;
        case "--chargen":
            if (++i >= args.Length) { Console.Error.WriteLine("--chargen needs a ROM file"); return 2; }
            chargen = args[i];
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

if (chargen != null)
{
    try { interpreter.Bus.LoadCharacterRom(File.ReadAllBytes(chargen)); }
    catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException)
    {
        Console.Error.WriteLine($"--chargen: {e.Message}");
        return 1;
    }
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
interpreter.Bus.Sound.Model = sidModel;
return SdlHost.Run(interpreter, screen, worker, scale, fullscreen, snapshot, joyPort, stateFile, resume);
