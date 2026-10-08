using C64Basic.Core.Machine;

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

    /// <summary>
    /// Called once when the interpreter is created. A host that shows the screen subscribes to
    /// <see cref="Bus.Written"/> to react to POKEs and keeps screen RAM, colour RAM and the cursor
    /// variables on the bus up to date, so PEEK sees what was printed.
    /// </summary>
    void Attach(Bus bus) { }
}

/// <summary>Where LOAD, SAVE, VERIFY and OPEN read and write files.</summary>
public interface IFileSystem
{
    bool Exists(string path);
    string[] ReadAllLines(string path);
    void WriteAllLines(string path, IEnumerable<string> lines);

    /// <summary>Whole-file read for OPEN/INPUT#. Records are separated by CR (CHR$(13)), as on a C64 disk.</summary>
    string ReadAllText(string path) => string.Concat(ReadAllLines(path).Select(l => l + "\r"));

    /// <summary>Whole-file write for CLOSE on a file opened for output.</summary>
    void WriteAllText(string path, string text)
    {
        var parts = text.Split('\r').ToList();
        if (parts[^1].Length == 0) parts.RemoveAt(parts.Count - 1);
        WriteAllLines(path, parts);
    }
}

public sealed class HostFileSystem : IFileSystem
{
    public bool Exists(string path) => File.Exists(path);
    public string[] ReadAllLines(string path) => File.ReadAllLines(path);
    public void WriteAllLines(string path, IEnumerable<string> lines) => File.WriteAllLines(path, lines);

    public string ReadAllText(string path) =>
        File.ReadAllText(path).Replace("\r\n", "\r").Replace('\n', '\r');

    public void WriteAllText(string path, string text) =>
        File.WriteAllText(path, text.Replace("\r", Environment.NewLine));
}
