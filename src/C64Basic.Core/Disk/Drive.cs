using System.Text;
using C64Basic.Core.Runtime;

namespace C64Basic.Core.Disk;

public enum FileType { Del, Seq, Prg, Usr, Rel }

/// <summary>One line of a disk directory.</summary>
public sealed record DirectoryEntry(string Name, FileType Type, int Blocks, bool Closed = true, bool Locked = false);

/// <summary>A file read from a drive. <see cref="Data"/> includes the two-byte load address for programs.</summary>
public sealed record DriveFile(string Name, FileType Type, byte[] Data);

/// <summary>An error the disk drive would report on its error channel (<c>62, FILE NOT FOUND,00,00</c>).</summary>
public sealed class DriveException : Exception
{
    public int Code { get; }
    public int Track { get; }
    public int Sector { get; }

    public DriveException(int code, int track = 0, int sector = 0) : base(DriveStatus.MessageFor(code))
    {
        Code = code;
        Track = track;
        Sector = sector;
    }
}

/// <summary>The drive's error-channel state.</summary>
public readonly record struct DriveStatus(int Code, string Message, int Track = 0, int Sector = 0)
{
    public static readonly DriveStatus Ok = new(0, "OK");

    public static DriveStatus Of(int code, int track = 0, int sector = 0) => new(code, MessageFor(code), track, sector);

    public static DriveStatus Scratched(int count) => new(1, "FILES SCRATCHED", count);

    public override string ToString() => $"{Code:00}, {Message},{Track:00},{Sector:00}";

    public static string MessageFor(int code) => code switch
    {
        0 => "OK",
        1 => "FILES SCRATCHED",
        20 => "READ ERROR",
        26 => "WRITE PROTECT ON",
        30 => "SYNTAX ERROR",
        31 => "SYNTAX ERROR",
        32 => "SYNTAX ERROR",
        33 => "SYNTAX ERROR",
        34 => "SYNTAX ERROR",
        39 => "FILE NOT FOUND",
        50 => "RECORD NOT PRESENT",
        51 => "OVERFLOW IN RECORD",
        52 => "FILE TOO LARGE",
        60 => "WRITE FILE OPEN",
        61 => "FILE NOT OPEN",
        62 => "FILE NOT FOUND",
        63 => "FILE EXISTS",
        64 => "FILE TYPE MISMATCH",
        65 => "NO BLOCK",
        66 => "ILLEGAL TRACK AND SECTOR",
        67 => "ILLEGAL SYSTEM T OR S",
        70 => "NO CHANNEL",
        71 => "DIR ERROR",
        72 => "DISK FULL",
        73 => "CBM DOS V2.6 1541",
        74 => "DRIVE NOT READY",
        _ => "UNKNOWN ERROR",
    };
}

/// <summary>
/// A disk drive (or tape deck): a directory of named files. Programs are read and written as PRG bytes
/// (two-byte load address first); sequential files as text with CR-separated records.
/// </summary>
public interface IDiskDrive
{
    string Title { get; }
    string Id { get; }
    int BlocksFree { get; }

    IReadOnlyList<DirectoryEntry> Directory();

    /// <summary>The first file matching the pattern (<c>*</c> and <c>?</c> allowed) and, if given, type. Throws code 62 when none does.</summary>
    DriveFile Read(string pattern, FileType? type = null);

    /// <summary>Stores a file. Without <paramref name="replace"/> an existing name is error 63.</summary>
    void Write(string name, FileType type, byte[] data, bool replace);

    /// <summary>Deletes every file matching the pattern and returns how many there were.</summary>
    int Scratch(string pattern);

    void Rename(string from, string to);

    /// <summary>Erases the disk and gives it a new name and, if given, ID.</summary>
    void Format(string name, string? id);

    /// <summary>Rebuilds the block allocation map from the files that exist.</summary>
    void Validate();

    // ---- relative files (disk images only) ----

    /// <summary>
    /// Opens a relative file. With a record length a missing file is created; without one a file that is not relative, or does not
    /// exist, gives null so the caller can treat it as an ordinary file. A drive without relative files refuses with error 64.
    /// </summary>
    IRelFile? OpenRel(string name, int recordLength) => recordLength > 0 ? throw new DriveException(64) : null;

    // ---- direct access to blocks (disk images only) ----

    /// <summary>One 256-byte block, including its two link bytes.</summary>
    byte[] ReadBlock(int track, int sector) => throw new DriveException(66, track, sector);

    void WriteBlock(int track, int sector, byte[] data) => throw new DriveException(66, track, sector);

    /// <summary>Marks a block used in the allocation map; error 65 (with the next free block) if it already is.</summary>
    void AllocateBlock(int track, int sector) => throw new DriveException(66, track, sector);

    void FreeBlock(int track, int sector) => throw new DriveException(66, track, sector);

    // ---- conveniences with sensible defaults ----

    /// <summary>A sequential file as text (PETSCII bytes mapped to characters, CR between records).</summary>
    string ReadText(string name) => DosText.Decode(Read(name).Data);

    void WriteText(string name, string text, bool replace) => Write(name, FileType.Seq, DosText.Encode(text), replace);

    /// <summary>Lets a drive that keeps programs as source text hand them over untokenized. Returns null if it has none.</summary>
    string[]? ReadProgramText(string name) => null;

    /// <summary>Saves a BASIC program; <paramref name="listing"/> is its source, for drives that store text.</summary>
    void WriteProgram(string name, byte[] prg, IReadOnlyList<string> listing, bool replace) =>
        Write(name, FileType.Prg, prg, replace);
}

/// <summary>Conversions between the interpreter's strings and PETSCII bytes, and DOS file name matching.</summary>
public static class DosText
{
    public static byte ToByte(char c)
    {
        int code = Petscii.ToCode(c);
        return code is >= 0 and <= 255 ? (byte)code : (byte)'?';
    }

    public static byte[] Encode(string text)
    {
        var bytes = new byte[text.Length];
        for (int i = 0; i < text.Length; i++) bytes[i] = ToByte(text[i]);
        return bytes;
    }

    public static string Decode(ReadOnlySpan<byte> bytes)
    {
        var sb = new StringBuilder(bytes.Length);
        foreach (byte b in bytes) sb.Append(Petscii.ToChar(b));
        return sb.ToString();
    }

    /// <summary>DOS pattern match: <c>?</c> is any one character, <c>*</c> matches the rest. Case-insensitive.</summary>
    public static bool Matches(string pattern, string name)
    {
        for (int i = 0; i < pattern.Length; i++)
        {
            if (pattern[i] == '*') return true;
            if (i >= name.Length) return false;
            if (pattern[i] != '?' && char.ToUpperInvariant(pattern[i]) != char.ToUpperInvariant(name[i])) return false;
        }
        return pattern.Length == name.Length;
    }

    public static bool HasWildcard(string name) => name.AsSpan().IndexOfAny('*', '?') >= 0;

    /// <summary>Throws the DOS error for a name that cannot be stored.</summary>
    public static void ValidateName(string name)
    {
        if (name.Length == 0) throw new DriveException(34);
        if (name.Length > 16 || name.IndexOfAny(new[] { '*', '?', ',', ':', '=' }) >= 0) throw new DriveException(33);
    }
}

/// <summary>The buffers of open direct-access channels (OPEN 5,8,5,"#"), which the block commands read and write.</summary>
public interface IBlockChannels
{
    /// <summary>The 256-byte buffer of a channel (its secondary address), or null if none is open.</summary>
    byte[]? Buffer(int channel);

    void SetPointer(int channel, int pointer);

    /// <summary>
    /// The P command: positions a relative file's channel at a record (1-based) and a byte in it (1-based). Returns the status
    /// (50 if the record is past the end of the file), or null if the channel is not a relative file.
    /// </summary>
    DriveStatus? Position(int channel, int record, int position);
}

/// <summary>A relative file: fixed-length records, addressed by number.</summary>
public interface IRelFile
{
    int RecordLength { get; }
    int RecordCount { get; }

    /// <summary>A record (0-based) as <see cref="RecordLength"/> bytes; error 50 if it does not exist.</summary>
    byte[] Read(int record);

    /// <summary>Stores a record (0-based), growing the file with empty records if it is past the end.</summary>
    void Write(int record, byte[] data);
}
