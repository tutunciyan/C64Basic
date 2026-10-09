using C64Basic.Core.Disk;

namespace C64Basic.Core.Rom;

/// <summary>
/// A disk as the drive's read head sees it: the raw GCR bit stream of up to 84 half-tracks (slot 0 is track 1, slot 1 is half-track
/// 1.5, slot 2 is track 2 ...), each with its speed zone. It is what a G64 image holds; a D64 is turned into one by GCR-encoding its
/// sectors. A track is a loop: the bits go round and round under the head.
/// </summary>
public sealed class GcrDisk
{
    public const int Slots = 84;
    const string Signature = "GCR-1541";
    const int HeaderSize = 12;

    /// <summary>The bytes of each half-track (null where nothing was recorded); bit 7 of byte 0 is the first bit under the head.</summary>
    public byte[]?[] Tracks { get; } = new byte[Slots][];

    /// <summary>The speed zone (0 = slowest, tracks 31-35, up to 3 = fastest, tracks 1-17) each half-track was recorded at, or -1 when <see cref="SpeedTables"/> says per byte.</summary>
    public int[] Speeds { get; } = new int[Slots];

    /// <summary>Per-byte speed zones (two bits each, four to a byte, first byte in the lowest bits) for tracks that change speed along the way.</summary>
    public byte[]?[] SpeedTables { get; } = new byte[Slots][];

    /// <summary>The write-protect notch: the drive's sensor reads it.</summary>
    public bool WriteProtected { get; set; }

    /// <summary>Set when the drive wrote to the disk.</summary>
    public bool Modified { get; set; }

    public int TrackBytes(int slot) => Tracks[slot]?.Length ?? 0;

    /// <summary>The speed zone under the head at a byte of a half-track.</summary>
    public int SpeedAt(int slot, int byteIndex)
    {
        int speed = Speeds[slot];
        if (speed >= 0) return speed;
        var table = SpeedTables[slot];
        if (table == null) return 0;
        int i = byteIndex >> 2;
        return i < table.Length ? table[i] >> ((byteIndex & 3) * 2) & 3 : 0;
    }

    public static bool IsG64(ReadOnlySpan<byte> data) => G64Image.IsG64(data);

    /// <summary>Reads a G64 image. Track data is taken exactly as stored, including half-tracks and tracks beyond 35.</summary>
    public static GcrDisk FromG64(byte[] g64)
    {
        if (!G64Image.IsG64(g64)) throw new InvalidDataException("not a G64 image");
        var disk = new GcrDisk();
        int slots = Math.Min((int)g64[9], Slots);
        for (int slot = 0; slot < slots; slot++)
        {
            int offset = BitConverter.ToInt32(g64, HeaderSize + slot * 4);
            if (offset <= 0 || offset + 2 > g64.Length) continue;
            int length = g64[offset] | g64[offset + 1] << 8;
            if (length == 0 || offset + 2 + length > g64.Length) continue;
            disk.Tracks[slot] = g64.AsSpan(offset + 2, length).ToArray();

            int speed = BitConverter.ToInt32(g64, HeaderSize + slots * 4 + slot * 4);
            if (speed <= 3) disk.Speeds[slot] = speed;
            else
            {
                disk.Speeds[slot] = -1;
                int tableLength = (length + 3) / 4;
                if (speed > 0 && speed + tableLength <= g64.Length) disk.SpeedTables[slot] = g64.AsSpan(speed, tableLength).ToArray();
            }
        }
        return disk;
    }

    static readonly int[] ZoneBytes = { 6250, 6666, 7142, 7692 };

    /// <summary>The track under a half-track slot, made (blank, at the speed of its zone) when nothing was recorded there.</summary>
    public byte[] EnsureTrack(int slot)
    {
        if (Tracks[slot] != null) return Tracks[slot]!;
        int track = slot / 2 + 1;
        int zone = track <= 17 ? 3 : track <= 24 ? 2 : track <= 30 ? 1 : 0;
        Speeds[slot] = zone;
        return Tracks[slot] = new byte[ZoneBytes[zone]];
    }

    const int MaxTrackBytes = 7928;

    /// <summary>The disk as a G64 image: every track as it is now, written changes included.</summary>
    public byte[] ToG64()
    {
        int blockSize = Math.Max(MaxTrackBytes, Tracks.Max(t => t?.Length ?? 0));
        var output = new byte[HeaderSize + Slots * 8 + Slots * (2 + blockSize)];
        System.Text.Encoding.ASCII.GetBytes(Signature).CopyTo(output, 0);
        output[9] = Slots;
        BitConverter.GetBytes((ushort)blockSize).CopyTo(output, 10);
        int at = HeaderSize + Slots * 8, used = at;
        for (int slot = 0; slot < Slots; slot++)
        {
            var track = Tracks[slot];
            if (track == null) continue;
            BitConverter.GetBytes(at).CopyTo(output, HeaderSize + slot * 4);
            int speed = Speeds[slot];
            if (speed < 0) speed = 0;                          // a per-byte speed table is not written back; the track keeps one zone
            BitConverter.GetBytes(speed).CopyTo(output, HeaderSize + Slots * 4 + slot * 4);
            output[at] = (byte)(track.Length & 0xFF); output[at + 1] = (byte)(track.Length >> 8);
            track.CopyTo(output, at + 2);
            at += 2 + blockSize;
            used = at;
        }
        return output.AsSpan(0, used).ToArray();
    }

    public void SaveState(BinaryWriter w)
    {
        w.Write(WriteProtected); w.Write(Modified);
        for (int slot = 0; slot < Slots; slot++)
        {
            var track = Tracks[slot];
            w.Write(track != null);
            if (track != null) { w.Write(track.Length); w.Write(track); }
            w.Write(Speeds[slot]);
            var table = SpeedTables[slot];
            w.Write(table != null);
            if (table != null) { w.Write(table.Length); w.Write(table); }
        }
    }

    public static GcrDisk LoadState(BinaryReader r)
    {
        var disk = new GcrDisk { WriteProtected = r.ReadBoolean(), Modified = r.ReadBoolean() };
        for (int slot = 0; slot < Slots; slot++)
        {
            if (r.ReadBoolean()) disk.Tracks[slot] = Machine.Bus.ReadExact(r, CheckedLength(r.ReadInt32()));
            disk.Speeds[slot] = r.ReadInt32();
            if (r.ReadBoolean()) disk.SpeedTables[slot] = Machine.Bus.ReadExact(r, CheckedLength(r.ReadInt32()));
        }
        return disk;
    }

    static int CheckedLength(int length) =>
        length is >= 0 and <= 65536 ? length : throw new InvalidDataException("a track in the saved state has an impossible length");

    /// <summary>The sectors of tracks 1-35 as a D64 image, plus a report of anything that did not read cleanly.</summary>
    public (byte[] Disk, G64Report Report) ToD64() => G64Image.Decode(ToG64());

    /// <summary>A D64 as it would sit on the disk: ordinary GCR sectors on tracks 1-35.</summary>
    public static GcrDisk FromD64(byte[] d64) => FromG64(G64Image.Encode(d64));

    /// <summary>Opens either kind of image by its content.</summary>
    public static GcrDisk FromImage(byte[] data) => IsG64(data) ? FromG64(data) : FromD64(data);
}
