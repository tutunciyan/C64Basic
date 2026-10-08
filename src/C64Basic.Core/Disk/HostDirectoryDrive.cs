using C64Basic.Core.IO;

namespace C64Basic.Core.Disk;

/// <summary>
/// A "disk" that is the host's working directory. <c>.prg</c> files are PRG bytes, <c>.bas</c> files are program
/// source text (what SAVE writes when the name has no extension), and any other file is a sequential text file.
/// </summary>
public sealed class HostDirectoryDrive : IDiskDrive
{
    readonly IFileSystem _fs;

    public HostDirectoryDrive(IFileSystem fs, string title = "HOST DIRECTORY")
    {
        _fs = fs;
        Title = title;
    }

    public string Title { get; }
    public string Id => "00";
    public int BlocksFree => 664;

    static bool Is(string path, string ext) => path.EndsWith(ext, StringComparison.OrdinalIgnoreCase);

    static string LogicalName(string file) =>
        Is(file, ".prg") || Is(file, ".bas") ? Path.GetFileNameWithoutExtension(file) : file;

    static FileType TypeOf(string file) => Is(file, ".prg") || Is(file, ".bas") ? FileType.Prg : FileType.Seq;

    public IReadOnlyList<DirectoryEntry> Directory()
    {
        var list = new List<DirectoryEntry>();
        foreach (var file in _fs.ListFiles().OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            int length;
            try { length = _fs.ReadAllBytes(file).Length; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
            list.Add(new DirectoryEntry(LogicalName(file).ToUpperInvariant(), TypeOf(file), Math.Max(1, (length + 253) / 254)));
        }
        return list;
    }

    /// <summary>The path a name refers to: the name itself, then with .bas or .prg appended.</summary>
    string? Resolve(string pattern)
    {
        if (DosText.HasWildcard(pattern))
        {
            return _fs.ListFiles()
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault(f => DosText.Matches(pattern, LogicalName(f)));
        }
        foreach (var candidate in new[] { pattern, pattern + ".bas", pattern + ".prg" })
            if (_fs.Exists(candidate)) return candidate;
        return null;
    }

    public DriveFile Read(string pattern, FileType? type = null)
    {
        string path = Resolve(pattern) ?? throw new DriveException(62);
        var found = TypeOf(path);
        if (type != null && type != found) throw new DriveException(64);

        byte[] data = Is(path, ".bas") ? PrgFormat.FromText(_fs.ReadAllLines(path))
            : Is(path, ".prg") ? _fs.ReadAllBytes(path)
            : DosText.Encode(_fs.ReadAllText(path));
        return new DriveFile(LogicalName(path).ToUpperInvariant(), found, data);
    }

    public string ReadText(string name)
    {
        if (!_fs.Exists(name)) throw new DriveException(62);
        return _fs.ReadAllText(name);
    }

    public string[]? ReadProgramText(string name)
    {
        string? path = Resolve(name);
        return path != null && Is(path, ".bas") ? _fs.ReadAllLines(path) : null;
    }

    public void Write(string name, FileType type, byte[] data, bool replace)
    {
        string path = type == FileType.Seq ? name : Is(name, ".prg") ? name : name + ".prg";
        if (!replace && _fs.Exists(path)) throw new DriveException(63);
        if (type == FileType.Seq) _fs.WriteAllText(path, DosText.Decode(data));
        else _fs.WriteAllBytes(path, data);
    }

    public void WriteText(string name, string text, bool replace)
    {
        if (!replace && _fs.Exists(name)) throw new DriveException(63);
        _fs.WriteAllText(name, text);
    }

    public void WriteProgram(string name, byte[] prg, IReadOnlyList<string> listing, bool replace)
    {
        if (Is(name, ".prg")) { Write(name, FileType.Prg, prg, replace); return; }
        string path = Path.HasExtension(name) ? name : name + ".bas";
        if (!replace && _fs.Exists(path)) throw new DriveException(63);
        _fs.WriteAllLines(path, listing);
    }

    public int Scratch(string pattern)
    {
        int count = 0;
        foreach (var file in _fs.ListFiles().ToList())
        {
            if (!DosText.Matches(pattern, LogicalName(file)) && !DosText.Matches(pattern, file)) continue;
            _fs.Delete(file);
            count++;
        }
        return count;
    }

    public void Rename(string from, string to)
    {
        string path = Resolve(from) ?? throw new DriveException(62);
        string target = Path.HasExtension(to) ? to : to + Path.GetExtension(path);
        if (_fs.Exists(target)) throw new DriveException(63);
        _fs.WriteAllBytes(target, _fs.ReadAllBytes(path));
        _fs.Delete(path);
    }

    /// <summary>A host directory cannot be formatted.</summary>
    public void Format(string name, string? id) => throw new DriveException(26);

    public void Validate() { }
}
