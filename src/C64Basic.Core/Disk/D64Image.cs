using System.Text;

namespace C64Basic.Core.Disk;

/// <summary>
/// A 35-track 1541 disk image (<c>.d64</c>, 174848 bytes): block allocation map on track 18 sector 0, directory chain
/// from 18/1, files as chains of sectors. Reads and writes the real on-disk structures. Every change is passed to
/// <c>save</c> so the host can write the image back.
/// </summary>
public sealed partial class D64Image : IDiskDrive
{
    public const int Size = 174848, Tracks = 35, DirTrack = 18, BamSector = 0;

    const int EntrySize = 32, EntriesPerSector = 8, FileInterleave = 10, DirInterleave = 3;

    readonly byte[] _d;
    readonly Action<byte[]>? _save;

    public D64Image(byte[] image, Action<byte[]>? save = null)
    {
        if (image.Length < Size) throw new InvalidDataException("not a D64 image: too short");
        _d = image;
        _save = save;
    }

    /// <summary>A freshly formatted blank disk.</summary>
    public static D64Image Create(string name, string id, Action<byte[]>? save = null)
    {
        var image = new D64Image(new byte[Size], save);
        image.Format(name, id);
        return image;
    }

    public byte[] ToArray() => (byte[])_d.Clone();

    // ---------- geometry ----------
    public static int SectorsIn(int track) => track switch { <= 17 => 21, <= 24 => 19, <= 30 => 18, _ => 17 };

    static int Offset(int track, int sector)
    {
        if (track < 1 || track > Tracks || sector < 0 || sector >= SectorsIn(track)) throw new DriveException(66, track, sector);
        int index = 0;
        for (int t = 1; t < track; t++) index += SectorsIn(t);
        return (index + sector) * 256;
    }

    Span<byte> Sector(int track, int sector) => _d.AsSpan(Offset(track, sector), 256);

    void Changed() => _save?.Invoke(_d);

    // ---------- block allocation map ----------
    int BamBase(int track) => Offset(DirTrack, BamSector) + 4 * track;

    bool IsFree(int track, int sector) => (_d[BamBase(track) + 1 + sector / 8] >> (sector % 8) & 1) != 0;

    void SetFree(int track, int sector, bool free)
    {
        int b = BamBase(track), bit = 1 << sector % 8;
        bool was = IsFree(track, sector);
        if (free == was) return;
        if (free) _d[b + 1 + sector / 8] |= (byte)bit; else _d[b + 1 + sector / 8] &= (byte)~bit;
        _d[b] = (byte)(_d[b] + (free ? 1 : -1));
    }

    public int BlocksFree
    {
        get
        {
            int n = 0;
            for (int t = 1; t <= Tracks; t++) if (t != DirTrack) n += _d[BamBase(t)];
            return n;
        }
    }

    /// <summary>Takes the next free sector for a file, working outward from the directory track like DOS does.</summary>
    (int Track, int Sector) Allocate(int lastTrack, int lastSector, int interleave)
    {
        // continue on the same track first
        if (lastTrack > 0 && _d[BamBase(lastTrack)] > 0)
        {
            int n = SectorsIn(lastTrack), s = (lastSector + interleave) % n;
            for (int i = 0; i < n; i++, s = (s + 1) % n)
                if (IsFree(lastTrack, s)) { SetFree(lastTrack, s, false); return (lastTrack, s); }
        }
        for (int d = 1; d < Tracks; d++)
        {
            foreach (int t in new[] { DirTrack - d, DirTrack + d })
            {
                if (t < 1 || t > Tracks || _d[BamBase(t)] == 0) continue;
                for (int s = 0; s < SectorsIn(t); s++)
                    if (IsFree(t, s)) { SetFree(t, s, false); return (t, s); }
            }
        }
        throw new DriveException(72);
    }

    // ---------- header ----------
    static string Unpad(ReadOnlySpan<byte> bytes)
    {
        int n = bytes.Length;
        while (n > 0 && bytes[n - 1] == 0xA0) n--;
        return DosText.Decode(bytes[..n]);
    }

    static void Pad(Span<byte> field, string text)
    {
        field.Fill(0xA0);
        for (int i = 0; i < text.Length && i < field.Length; i++) field[i] = DosText.ToByte(text[i]);
    }

    public string Title => Unpad(Sector(DirTrack, BamSector).Slice(0x90, 16));
    public string Id => Unpad(Sector(DirTrack, BamSector).Slice(0xA2, 2));

    // ---------- directory ----------
    sealed record Slot(int Track, int Sector, int Index);

    IEnumerable<Slot> Slots()
    {
        int track = DirTrack, sector = 1, guard = 0;
        while (track != 0 && guard++ < 300)
        {
            int at = Offset(track, sector);
            int nextTrack = _d[at], nextSector = _d[at + 1];
            for (int i = 0; i < EntriesPerSector; i++) yield return new Slot(track, sector, i);
            track = nextTrack; sector = nextSector;
        }
    }

    Span<byte> EntryAt(Slot slot) => Sector(slot.Track, slot.Sector).Slice(slot.Index * EntrySize, EntrySize);

    static string EntryName(ReadOnlySpan<byte> e) => Unpad(e.Slice(5, 16));

    static FileType TypeOf(byte flags) => (FileType)Math.Min(flags & 7, 4);

    public IReadOnlyList<DirectoryEntry> Directory()
    {
        var list = new List<DirectoryEntry>();
        foreach (var slot in Slots())
        {
            var e = EntryAt(slot);
            if (e[2] == 0) continue;
            list.Add(new DirectoryEntry(EntryName(e), TypeOf(e[2]), e[30] | e[31] << 8, (e[2] & 0x80) != 0, (e[2] & 0x40) != 0));
        }
        return list;
    }

    Slot? Find(string pattern, FileType? type)
    {
        foreach (var slot in Slots())
        {
            var e = EntryAt(slot);
            if (e[2] == 0) continue;
            if (type != null && TypeOf(e[2]) != type) continue;
            if (DosText.Matches(pattern, EntryName(e))) return slot;
        }
        return null;
    }

    // ---------- files ----------
    public DriveFile Read(string pattern, FileType? type = null)
    {
        var slot = Find(pattern, type) ?? throw new DriveException(62);
        var e = EntryAt(slot);
        var data = new List<byte>();
        int track = e[3], sector = e[4], guard = 0;
        while (track != 0)
        {
            if (guard++ > Tracks * 21) throw new DriveException(20, track, sector);
            var s = Sector(track, sector);
            int next = s[0];
            if (next == 0) { for (int i = 2; i <= s[1]; i++) data.Add(s[i]); break; }
            for (int i = 2; i < 256; i++) data.Add(s[i]);
            track = next; sector = s[1];
        }
        return new DriveFile(EntryName(e), TypeOf(e[2]), data.ToArray());
    }

    public void Write(string name, FileType type, byte[] data, bool replace)
    {
        DosText.ValidateName(name);
        var existing = Find(name, null);
        if (existing != null)
        {
            if (!replace) throw new DriveException(63);
            FreeChain(EntryAt(existing));
            EntryAt(existing)[2..].Clear();
        }

        int sectors = Math.Max(1, (data.Length + 253) / 254);
        // the directory entry may need a new sector too; DOS allocates it first
        if (sectors > BlocksFree) throw new DriveException(72);

        var slot = FreeSlot();
        int firstTrack = 0, firstSector = 0, track = 0, sector = 0, offset = 0;
        for (int n = 0; n < sectors; n++)
        {
            var (t, s) = Allocate(track, sector, FileInterleave);
            if (n == 0) { firstTrack = t; firstSector = s; }
            else { Sector(track, sector)[0] = (byte)t; Sector(track, sector)[1] = (byte)s; }
            track = t; sector = s;

            var buffer = Sector(t, s);
            buffer.Clear();
            int count = Math.Min(254, data.Length - offset);
            data.AsSpan(offset, count).CopyTo(buffer[2..]);
            offset += count;
            if (n == sectors - 1) { buffer[0] = 0; buffer[1] = (byte)(count + 1); }
        }

        var entry = EntryAt(slot);
        entry[2..].Clear();
        entry[2] = (byte)(0x80 | (int)type);
        entry[3] = (byte)firstTrack; entry[4] = (byte)firstSector;
        Pad(entry.Slice(5, 16), name);
        entry[30] = (byte)sectors; entry[31] = (byte)(sectors >> 8);
        Changed();
    }

    Slot FreeSlot()
    {
        Slot? last = null;
        foreach (var slot in Slots())
        {
            last = slot;
            if (EntryAt(slot)[2] == 0) return slot;
        }
        // directory full: chain a new sector after the last one
        var (t, s) = (DirTrack, 0);
        var tail = last!;
        int n = SectorsIn(DirTrack), start = (tail.Sector + DirInterleave) % n;
        bool found = false;
        for (int i = 0; i < n && !found; i++)
        {
            s = (start + i) % n;
            found = s != BamSector && IsFree(DirTrack, s);
        }
        if (!found) throw new DriveException(72);
        SetFree(DirTrack, s, false);
        Sector(tail.Track, tail.Sector)[0] = (byte)t;
        Sector(tail.Track, tail.Sector)[1] = (byte)s;
        var fresh = Sector(t, s);
        fresh.Clear();
        fresh[1] = 0xFF;
        return new Slot(t, s, 0);
    }

    void FreeChain(Span<byte> entry)
    {
        if ((entry[2] & 7) == RelType) FreeSideChain(entry);
        int track = entry[3], sector = entry[4], guard = 0;
        while (track != 0 && guard++ <= Tracks * 21)
        {
            var s = Sector(track, sector);
            SetFree(track, sector, true);
            if (s[0] == 0) break;
            track = s[0]; sector = s[1];
        }
    }

    // ---------- direct access ----------
    public byte[] ReadBlock(int track, int sector) => Sector(track, sector).ToArray();

    public void WriteBlock(int track, int sector, byte[] data)
    {
        var block = Sector(track, sector);
        block.Clear();
        data.AsSpan(0, Math.Min(256, data.Length)).CopyTo(block);
        Changed();
    }

    public void AllocateBlock(int track, int sector)
    {
        Offset(track, sector); // range check
        if (!IsFree(track, sector))
        {
            // DOS suggests the next free block after this one
            for (int t = track; t <= Tracks; t++)
                for (int s = t == track ? sector + 1 : 0; s < SectorsIn(t); s++)
                    if (IsFree(t, s)) throw new DriveException(65, t, s);
            throw new DriveException(65, 0, 0);
        }
        SetFree(track, sector, false);
        Changed();
    }

    public void FreeBlock(int track, int sector)
    {
        Offset(track, sector);
        SetFree(track, sector, true);
        Changed();
    }

    public int Scratch(string pattern)
    {
        int count = 0;
        foreach (var slot in Slots().ToList())
        {
            var e = EntryAt(slot);
            if (e[2] == 0 || !DosText.Matches(pattern, EntryName(e))) continue;
            if ((e[2] & 0x40) != 0) continue; // locked files survive
            FreeChain(e);
            e[2] = 0;
            count++;
        }
        if (count > 0) Changed();
        return count;
    }

    public void Rename(string from, string to)
    {
        DosText.ValidateName(to);
        if (Find(to, null) != null) throw new DriveException(63);
        var slot = Find(from, null) ?? throw new DriveException(62);
        Pad(EntryAt(slot).Slice(5, 16), to);
        Changed();
    }

    public void Format(string name, string? id)
    {
        string keepId = id ?? (Id.Length == 2 && Id.All(c => c >= ' ') ? Id : "00");
        Array.Clear(_d);
        var bam = Sector(DirTrack, BamSector);
        bam[0] = DirTrack; bam[1] = 1; bam[2] = 0x41;
        for (int t = 1; t <= Tracks; t++)
        {
            int n = SectorsIn(t), b = 4 * t;
            bam[b] = (byte)n;
            for (int s = 0; s < n; s++) bam[b + 1 + s / 8] |= (byte)(1 << s % 8);
        }
        Pad(bam.Slice(0x90, 16), name);
        bam[0xA0] = bam[0xA1] = 0xA0;
        Pad(bam.Slice(0xA2, 2), keepId);
        bam[0xA4] = 0xA0; bam[0xA5] = (byte)'2'; bam[0xA6] = (byte)'A';
        for (int i = 0xA7; i <= 0xAA; i++) bam[i] = 0xA0;
        SetFree(DirTrack, BamSector, false);
        SetFree(DirTrack, 1, false);
        Sector(DirTrack, 1)[1] = 0xFF;
        Changed();
    }

    public void Validate()
    {
        for (int t = 1; t <= Tracks; t++)
        {
            int n = SectorsIn(t), b = BamBase(t);
            _d[b] = (byte)n;
            for (int i = 1; i <= 3; i++) _d[b + i] = 0;
            for (int s = 0; s < n; s++) _d[b + 1 + s / 8] |= (byte)(1 << s % 8);
        }
        SetFree(DirTrack, BamSector, false);
        int track = DirTrack, sector = 1, guard = 0;
        while (track != 0 && guard++ < 300)
        {
            SetFree(track, sector, false);
            var s = Sector(track, sector);
            track = s[0]; sector = s[1];
        }
        foreach (var slot in Slots())
        {
            var e = EntryAt(slot);
            if (e[2] == 0) continue;
            if ((e[2] & 7) == RelType) MarkSideChain(e);
            int t = e[3], sc = e[4], g = 0;
            while (t != 0 && g++ <= Tracks * 21)
            {
                SetFree(t, sc, false);
                var s = Sector(t, sc);
                t = s[0]; sc = s[1];
            }
        }
        Changed();
    }
}
