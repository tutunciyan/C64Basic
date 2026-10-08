using System.Text;
using C64Basic.Core.IO;
using C64Basic.Core.Runtime;

namespace C64Basic.Tests;

public sealed class TestConsole : IConsoleDevice
{
    readonly StringBuilder _out = new();
    readonly Queue<string> _input;

    public TestConsole(IEnumerable<string>? input = null) => _input = new Queue<string>(input ?? Array.Empty<string>());

    public string Output => _out.ToString();
    public bool BreakRequested { get; set; }

    /// <summary>Simulates the user pressing RUN/STOP once this many Write calls have happened.</summary>
    public int BreakAfterWrites { get; set; }

    int _writes;

    public void Write(string text)
    {
        _out.Append(text);
        if (BreakAfterWrites > 0 && ++_writes == BreakAfterWrites) BreakRequested = true;
    }

    public string? ReadLine() => _input.Count > 0 ? _input.Dequeue() : null;
    public string GetKey() => "";
}

public sealed class MemoryFileSystem : IFileSystem
{
    public Dictionary<string, string[]> Files { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Files written as raw bytes (PRG files, disk images).</summary>
    public Dictionary<string, byte[]> Binary { get; } = new(StringComparer.OrdinalIgnoreCase);

    public bool Exists(string path) => Files.ContainsKey(path) || Binary.ContainsKey(path);
    public string[] ReadAllLines(string path) => Files[path];
    public void WriteAllLines(string path, IEnumerable<string> lines) { Binary.Remove(path); Files[path] = lines.ToArray(); }
    public byte[] ReadAllBytes(string path) => Binary.TryGetValue(path, out var b) ? b : System.Text.Encoding.Latin1.GetBytes(string.Concat(Files[path].Select(l => l + "\r")));
    public void WriteAllBytes(string path, byte[] data) { Files.Remove(path); Binary[path] = data; }
    public IEnumerable<string> ListFiles() => Files.Keys.Concat(Binary.Keys).ToList();
    public void Delete(string path) { Files.Remove(path); Binary.Remove(path); }
}

public static class Basic
{
    /// <summary>Types each line at the prompt and returns everything the interpreter printed.</summary>
    public static string Run(params string[] lines) => RunWith(null, null, lines);

    public static string RunStrict(params string[] lines) => RunWith(null, new InterpreterOptions { Strict = true }, lines);

    public static string RunWith(string[]? input, InterpreterOptions? options, params string[] lines)
    {
        var console = new TestConsole(input);
        var interp = new Interpreter(console, new MemoryFileSystem(), options);
        foreach (var line in lines) interp.ProcessLine(line);
        return console.Output;
    }

    public static (string Output, Interpreter Interp, MemoryFileSystem Fs) Session(params string[] lines)
    {
        var console = new TestConsole();
        var fs = new MemoryFileSystem();
        var interp = new Interpreter(console, fs);
        foreach (var line in lines) interp.ProcessLine(line);
        return (console.Output, interp, fs);
    }

    /// <summary>Output with all line endings normalised to \n.</summary>
    public static string Lines(params string[] lines) => string.Join("\n", lines) + "\n";
}
