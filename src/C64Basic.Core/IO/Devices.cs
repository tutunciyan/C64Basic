namespace C64Basic.Core.IO;

/// <summary>The "screen and keyboard" the interpreter talks to.</summary>
public interface IConsoleDevice
{
    /// <summary>Writes text. '\n' is a line break; CHR$(147) (U+0093) means clear screen.</summary>
    void Write(string text);

    /// <summary>Reads one line of input, or null when input has ended.</summary>
    string? ReadLine();

    /// <summary>Non-blocking key read for GET. Returns "" when no key is waiting.</summary>
    string GetKey();

    /// <summary>Set by the host when the user presses RUN/STOP (Ctrl+C).</summary>
    bool BreakRequested { get; set; }

    /// <summary>Called after every POKE so the host can react to special addresses.</summary>
    void Poke(int address, int value) { }
}

/// <summary>Where LOAD, SAVE and VERIFY read and write program text.</summary>
public interface IFileSystem
{
    bool Exists(string path);
    string[] ReadAllLines(string path);
    void WriteAllLines(string path, IEnumerable<string> lines);
}

public sealed class HostFileSystem : IFileSystem
{
    public bool Exists(string path) => File.Exists(path);
    public string[] ReadAllLines(string path) => File.ReadAllLines(path);
    public void WriteAllLines(string path, IEnumerable<string> lines) => File.WriteAllLines(path, lines);
}
