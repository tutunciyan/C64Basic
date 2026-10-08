using C64Basic.Console;
using C64Basic.Core;
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
      -h, --help      show this help
    """;

bool strict = false, plain = false, fast = false;
int? width = null;
string? file = null;

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
