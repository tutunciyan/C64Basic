namespace C64Basic.Core.Disk;

/// <summary>What reading a G64 turned up: the sectors it held and anything that suggests copy protection.</summary>
public sealed class G64Report
{
    public int TracksStored, HalfTracksStored, ExtraTracks, SectorsFound, SectorsMissing, BadHeaders, BadDataChecksums, DuplicateSectors;
    public bool NonStandardSpeed;
    public readonly List<string> Notes = new();

    /// <summary>True when every one of the 683 sectors of a standard 35-track disk was read cleanly and nothing looked unusual.</summary>
    public bool Clean => SectorsMissing == 0 && BadHeaders == 0 && BadDataChecksums == 0 && DuplicateSectors == 0
                         && HalfTracksStored == 0 && ExtraTracks == 0 && !NonStandardSpeed;

    public override string ToString() => string.Join("\n", new[]
    {
        $"{TracksStored} tracks stored ({HalfTracksStored} half-tracks, {ExtraTracks} beyond track 35)",
        $"{SectorsFound} of {D64Image.Sectors} sectors read, {SectorsMissing} missing, {BadHeaders} bad headers, {BadDataChecksums} bad data checksums, {DuplicateSectors} duplicates",
    }.Concat(Notes));
}

/// <summary>
/// The G64 disk image: the raw GCR bit stream of every track as a 1541 reads it (header 'GCR-1541', 84 half-track slots).
/// <see cref="Decode"/> turns the sectors in it into an ordinary 35-track D64 image, which this project's drive can use. A
/// loader that talks to the drive's own processor, or protection that lives in the bit stream itself (odd sync lengths, half
/// tracks, bad checksums), cannot be reproduced that way, and the report says what was found.
/// </summary>
public static class G64Image
{
    const string Signature = "GCR-1541";
    const int HeaderSize = 12, Slots = 84, MaxTrackBytes = 7928;

    // five GCR bits per nibble
    static readonly byte[] Gcr = { 0x0A, 0x0B, 0x12, 0x13, 0x0E, 0x0F, 0x16, 0x17, 0x09, 0x19, 0x1A, 0x1B, 0x0D, 0x1D, 0x1E, 0x15 };
    static readonly int[] Ungcr = BuildUngcr();

    static int[] BuildUngcr()
    {
        var t = new int[32];
        Array.Fill(t, -1);
        for (int n = 0; n < 16; n++) t[Gcr[n]] = n;
        return t;
    }

    public static bool IsG64(ReadOnlySpan<byte> data) =>
        data.Length >= HeaderSize + Slots * 8 && System.Text.Encoding.ASCII.GetString(data[..8]) == Signature;

    // ---------- reading ----------
    public static (byte[] Disk, G64Report Report) Decode(byte[] g64)
    {
        if (!IsG64(g64)) throw new InvalidDataException("not a G64 image");
        var report = new G64Report();
        var disk = new byte[D64Image.Size];
        var seen = new bool[D64Image.Sectors];
        int slots = Math.Min((int)g64[9], Slots);
        if (g64[8] != 0) report.Notes.Add($"G64 version {g64[8]}");

        for (int slot = 0; slot < slots; slot++)
        {
            int offset = BitConverter.ToInt32(g64, HeaderSize + slot * 4);
            if (offset == 0) continue;
            if (offset < 0 || offset + 2 > g64.Length) { report.Notes.Add($"half-track {slot}: offset outside the file"); continue; }
            int length = g64[offset] | g64[offset + 1] << 8;
            if (length == 0 || offset + 2 + length > g64.Length) { report.Notes.Add($"half-track {slot}: bad length {length}"); continue; }

            report.TracksStored++;
            if (slot % 2 == 1) { report.HalfTracksStored++; report.Notes.Add($"data on half-track {slot / 2 + 1}.5"); continue; }
            int track = slot / 2 + 1;
            if (track > D64Image.Tracks) { report.ExtraTracks++; report.Notes.Add($"data on track {track} (beyond 35)"); continue; }

            int speed = BitConverter.ToInt32(g64, HeaderSize + slots * 4 + slot * 4);
            if (speed > 3) report.NonStandardSpeed = true;       // a per-byte speed table is a protection trick
            else if (speed != ZoneOf(track)) { report.NonStandardSpeed = true; report.Notes.Add($"track {track}: speed zone {speed}, expected {ZoneOf(track)}"); }

            DecodeTrack(g64.AsSpan(offset + 2, length), track, disk, seen, report);
        }

        for (int i = 0; i < seen.Length; i++) if (!seen[i]) report.SectorsMissing++;
        return (disk, report);
    }

    static int ZoneOf(int track) => track <= 17 ? 3 : track <= 24 ? 2 : track <= 30 ? 1 : 0;

    /// <summary>The disk's sector number (0-682) for a track and sector, or -1 if there is no such sector on a standard disk.</summary>
    static int SectorIndex(int track, int sector)
    {
        if (track < 1 || track > D64Image.Tracks || sector < 0 || sector >= D64Image.SectorsIn(track)) return -1;
        int index = sector;
        for (int t = 1; t < track; t++) index += D64Image.SectorsIn(t);
        return index;
    }

    static void DecodeTrack(ReadOnlySpan<byte> bytes, int track, byte[] disk, bool[] seen, G64Report report)
    {
        int bits = bytes.Length * 8;

        int Bit(ReadOnlySpan<byte> data, int p)
        {
            p %= bits;
            return data[p >> 3] >> (7 - (p & 7)) & 1;
        }

        int ReadGcrByte(ReadOnlySpan<byte> data, int p) // 10 bits -> one byte, or -1 for an invalid code
        {
            int hi = 0, lo = 0;
            for (int i = 0; i < 5; i++) hi = hi << 1 | Bit(data, p + i);
            for (int i = 5; i < 10; i++) lo = lo << 1 | Bit(data, p + i);
            int a = Ungcr[hi], b = Ungcr[lo];
            return a < 0 || b < 0 ? -1 : a << 4 | b;
        }

        byte[]? Block(ReadOnlySpan<byte> data, int p, int size) // `size` decoded bytes starting at bit p
        {
            var block = new byte[size];
            for (int i = 0; i < size; i++)
            {
                int v = ReadGcrByte(data, p + i * 10);
                if (v < 0) return null;
                block[i] = (byte)v;
            }
            return block;
        }

        int run = 0, pendingTrack = -1, pendingSector = -1, pendingStart = -1;
        // a second pass over the start so a block that straddles the end of the data is still seen
        int limit = bits + 400 * 8;
        for (int p = 0; p < limit; p++)
        {
            if (Bit(bytes, p) == 1) { run++; continue; }
            if (run < 10) { run = 0; continue; }
            run = 0;                                             // a sync mark ended: the next bit starts a block
            int first = ReadGcrByte(bytes, p);
            if (first == 0x08 && p < bits)
            {
                var h = Block(bytes, p, 8);
                if (h == null || (h[2] ^ h[3] ^ h[4] ^ h[5]) != h[1]) { report.BadHeaders++; pendingSector = -1; continue; }
                pendingSector = h[2]; pendingTrack = h[3]; pendingStart = p;
            }
            else if (first == 0x07 && pendingSector >= 0)
            {
                var d = Block(bytes, p, 260);
                if (d == null) { report.BadDataChecksums++; pendingSector = -1; continue; }
                int sum = 0;
                for (int i = 1; i <= 256; i++) sum ^= d[i];
                int index = SectorIndex(pendingTrack, pendingSector);
                if (sum != d[257]) report.BadDataChecksums++;
                else if (index < 0) report.Notes.Add($"track {track}: header names sector {pendingTrack}/{pendingSector}, which a standard disk does not have");
                else if (seen[index])
                {
                    if (pendingStart < bits && p < bits) report.DuplicateSectors++;   // the second pass over the wrap point is not a duplicate
                }
                else
                {
                    if (pendingTrack != track) report.Notes.Add($"track {track} holds sector {pendingTrack}/{pendingSector}");
                    Array.Copy(d, 1, disk, index * 256, 256);
                    seen[index] = true;
                    report.SectorsFound++;
                }
                pendingSector = -1;
            }
        }
    }

    // ---------- writing ----------
    static readonly int[] ZoneBytes = { 6250, 6666, 7142, 7692 };

    /// <summary>Encodes a D64 as a G64 with ordinary GCR sectors on tracks 1-35 (for tests and for exchanging images).</summary>
    public static byte[] Encode(byte[] d64)
    {
        if (d64.Length < D64Image.Size) throw new InvalidDataException("not a D64 image: too short");
        var image = new D64Image(d64);
        string id = image.Id;
        byte id1 = (byte)(id.Length > 0 ? id[0] : '0'), id2 = (byte)(id.Length > 1 ? id[1] : '0');

        var output = new byte[HeaderSize + Slots * 8 + D64Image.Tracks * (2 + MaxTrackBytes)];
        System.Text.Encoding.ASCII.GetBytes(Signature).CopyTo(output, 0);
        output[9] = Slots;
        BitConverter.GetBytes((ushort)MaxTrackBytes).CopyTo(output, 10);

        int at = HeaderSize + Slots * 8;
        for (int track = 1; track <= D64Image.Tracks; track++)
        {
            int slot = (track - 1) * 2;
            BitConverter.GetBytes(at).CopyTo(output, HeaderSize + slot * 4);
            BitConverter.GetBytes(ZoneOf(track)).CopyTo(output, HeaderSize + Slots * 4 + slot * 4);

            int size = ZoneBytes[ZoneOf(track)], sectors = D64Image.SectorsIn(track);
            var data = new byte[size];
            Array.Fill(data, (byte)0x55);
            int pos = 0, gap = Math.Max(4, (size - sectors * (5 + 10 + 9 + 5 + 325)) / sectors);
            for (int s = 0; s < sectors && pos + 354 <= size; s++)
            {
                for (int i = 0; i < 5; i++) data[pos++] = 0xFF;
                var header = new byte[] { 0x08, (byte)(s ^ track ^ id2 ^ id1), (byte)s, (byte)track, id2, id1, 0x0F, 0x0F };
                pos = Put(data, pos, header);
                pos += 9;                                                         // header gap (0x55)
                for (int i = 0; i < 5; i++) data[pos++] = 0xFF;
                var block = new byte[260];
                block[0] = 0x07;
                image.ReadBlock(track, s).CopyTo(block, 1);
                byte sum = 0;
                for (int i = 1; i <= 256; i++) sum ^= block[i];
                block[257] = sum;
                pos = Put(data, pos, block);
                pos += gap;                                                       // gap before the next sector (0x55)
            }
            output[at] = (byte)(size & 0xFF); output[at + 1] = (byte)(size >> 8);
            data.CopyTo(output, at + 2);
            at += 2 + MaxTrackBytes;
        }
        return output;
    }

    /// <summary>GCR-encodes bytes (four at a time become five) into the track at <paramref name="pos"/>.</summary>
    static int Put(byte[] track, int pos, byte[] bytes)
    {
        for (int i = 0; i < bytes.Length; i += 4)
        {
            long bits = 0;
            for (int k = 0; k < 4; k++)
                bits = bits << 10 | (long)Gcr[bytes[i + k] >> 4] << 5 | Gcr[bytes[i + k] & 15];
            for (int k = 4; k >= 0; k--) track[pos++] = (byte)(bits >> (k * 8));
        }
        return pos;
    }
}
