namespace C64Basic.Core.Rom;

/// <summary>
/// The ROM dumps ROM mode needs: the BASIC and KERNAL ROMs of the C64 (8 KB each), the 1541's DOS ROM (16 KB) and optionally the
/// character generator (4 KB). They are copyrighted and not part of this project; <see cref="Find"/> looks for them in a folder.
/// </summary>
public sealed class RomSet
{
    public byte[] Basic { get; }
    public byte[] Kernal { get; }
    public byte[] Dos { get; }
    public byte[]? Chargen { get; }

    /// <summary>What was found, one line per ROM (the file, and the part number when the checksum is a known one).</summary>
    public IReadOnlyList<string> Notes { get; }

    public RomSet(byte[] basic, byte[] kernal, byte[] dos, byte[]? chargen = null, IReadOnlyList<string>? notes = null)
    {
        if (basic.Length != 8192) throw new ArgumentException($"the BASIC ROM is 8192 bytes, not {basic.Length}", nameof(basic));
        if (kernal.Length != 8192) throw new ArgumentException($"the KERNAL ROM is 8192 bytes, not {kernal.Length}", nameof(kernal));
        if (dos.Length != 16384) throw new ArgumentException($"the 1541 DOS ROM is 16384 bytes, not {dos.Length}", nameof(dos));
        if (chargen != null && chargen.Length != 4096) throw new ArgumentException($"the character ROM is 4096 bytes, not {chargen.Length}", nameof(chargen));
        Basic = basic; Kernal = kernal; Dos = dos; Chargen = chargen;
        Notes = notes ?? Array.Empty<string>();
    }

    /// <summary>The file names looked for first; other files of the right size with a matching name prefix are accepted too.</summary>
    static readonly (string Role, string[] Names, string[] Prefixes, int Size)[] Wanted =
    {
        ("BASIC", new[] { "basic-901226-01.bin" }, new[] { "basic" }, 8192),
        ("KERNAL", new[] { "kernal-901227-03.bin" }, new[] { "kernal" }, 8192),
        ("1541 DOS", new[] { "dos1541ii-251968-03.bin" }, new[] { "dos1541", "1541", "dos" }, 16384),
        ("character", new[] { "chargen-901225-01.bin" }, new[] { "chargen", "char" }, 4096),
    };

    /// <summary>Known checksums (CRC32), so a note can say which ROM it is. Unknown ones are used all the same.</summary>
    static readonly Dictionary<uint, string> Known = new()
    {
        [0xF833D117] = "BASIC V2 901226-01",
        [0xDBE3E7C7] = "KERNAL 901227-03 (PAL/NTSC)",
        [0x899FA3C5] = "1541-II DOS 2.6 251968-03",
        [0xEC4272EE] = "character generator 901225-01",
    };

    /// <summary>Looks in <paramref name="directory"/>; throws <see cref="FileNotFoundException"/> naming what is missing.</summary>
    public static RomSet Find(string directory)
    {
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException($"ROM folder not found: {directory}");
        var found = new byte[Wanted.Length][];
        var notes = new List<string>();
        var missing = new List<string>();
        for (int i = 0; i < Wanted.Length; i++)
        {
            var (role, names, prefixes, size) = Wanted[i];
            string? path = null;
            foreach (string name in names)
            {
                string candidate = Path.Combine(directory, name);
                if (File.Exists(candidate) && new FileInfo(candidate).Length == size) { path = candidate; break; }
            }
            path ??= Directory.EnumerateFiles(directory)
                .Where(f => new FileInfo(f).Length == size)
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault(f => prefixes.Any(p => Path.GetFileName(f).StartsWith(p, StringComparison.OrdinalIgnoreCase)));
            if (path == null) { if (role != "character") missing.Add($"{role} ({size} bytes)"); continue; }
            found[i] = File.ReadAllBytes(path);
            uint crc = Crc32(found[i]);
            notes.Add($"{role}: {Path.GetFileName(path)}, {(Known.TryGetValue(crc, out var part) ? part : $"unrecognised, CRC {crc:X8}")}");
        }
        if (missing.Count > 0)
            throw new FileNotFoundException($"ROM mode needs ROM dumps in {directory}; missing: {string.Join(", ", missing)}");
        return new RomSet(found[0], found[1], found[2], found[3], notes);
    }

    public static uint Crc32(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte b in data)
        {
            crc ^= b;
            for (int k = 0; k < 8; k++) crc = (crc & 1) != 0 ? crc >> 1 ^ 0xEDB88320 : crc >> 1;
        }
        return ~crc;
    }
}
