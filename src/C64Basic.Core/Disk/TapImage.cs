using System.Text;

namespace C64Basic.Core.Disk;

/// <summary>
/// A <c>.tap</c> tape image: the raw pulse lengths the datasette produced, which this class decodes in the KERNAL's standard
/// tape format (leader, countdown, header block and data block, each recorded twice) and encodes the same way for SAVE.
/// Turbo loaders and other formats are kept in the image but not decoded. A tape cannot be edited in place: SAVE appends
/// (a later program with the same name wins when loading), and scratching or renaming is refused.
/// </summary>
public sealed class TapImage : IDiskDrive
{
    const string Signature = "C64-TAPE-RAW";
    const int HeaderSize = 20;

    // pulse lengths in units of 8 CPU cycles: a bit is two pulses, short+medium for 0 and medium+short for 1
    const int ShortPulse = 0x30, MediumPulse = 0x42, LongPulse = 0x56;
    const int ShortLimit = 0x39 * 8, MediumLimit = 0x4C * 8, GapLimit = 0x90 * 8;

    sealed class Item
    {
        public string Name = "";
        public byte[] Prg = Array.Empty<byte>();
    }

    readonly List<byte> _pulses = new();
    readonly List<Item> _items = new();
    readonly Action<byte[]>? _save;
    byte _version = 1;

    public string Title { get; private set; }
    public string Id => "";
    public int BlocksFree => 664;

    /// <summary>Opens an image, or starts an empty tape when <paramref name="image"/> is null.</summary>
    public TapImage(byte[]? image = null, string title = "TAPE", Action<byte[]>? save = null)
    {
        Title = title;
        _save = save;
        if (image == null) return;

        if (image.Length < HeaderSize || Encoding.ASCII.GetString(image, 0, Signature.Length) != Signature)
            throw new InvalidDataException("not a TAP tape image");
        _version = image[12];
        int size = image[16] | image[17] << 8 | image[18] << 16 | image[19] << 24;
        int available = Math.Min(size, image.Length - HeaderSize);
        for (int i = 0; i < available; i++) _pulses.Add(image[HeaderSize + i]);
        Decode();
    }

    /// <summary>The length in CPU cycles of every pulse on the tape, in order (what a datasette feeds the CIA's FLAG pin).</summary>
    public int[] PulseCycles()
    {
        var cycles = new List<int>(_pulses.Count);
        for (int i = 0; i < _pulses.Count; i++)
        {
            int length = _pulses[i] * 8;
            if (_pulses[i] == 0)
            {
                if (_version >= 1 && i + 3 < _pulses.Count) { length = _pulses[i + 1] | _pulses[i + 2] << 8 | _pulses[i + 3] << 16; i += 3; }
                else length = 256 * 8;
            }
            cycles.Add(length);
        }
        return cycles.ToArray();
    }

    /// <summary>Adds recorded pulses (lengths in CPU cycles) at the end of the tape, then reads what the tape holds again.</summary>
    public void AppendPulses(IEnumerable<int> cycles)
    {
        if (_version == 0) _version = 1;
        foreach (int length in cycles)
        {
            if (length <= 255 * 8) _pulses.Add((byte)Math.Max(1, (length + 4) / 8));
            else { _pulses.Add(0); _pulses.Add((byte)length); _pulses.Add((byte)(length >> 8)); _pulses.Add((byte)(length >> 16)); }
        }
        Decode();
        Changed();
    }

    // ---------- decoding ----------
    /// <summary>The pulse stream as S, M, L or G (a gap) in the order it occurs.</summary>
    string Classify()
    {
        var sb = new StringBuilder(_pulses.Count);
        for (int i = 0; i < _pulses.Count; i++)
        {
            int cycles = _pulses[i] * 8;
            if (_pulses[i] == 0)
            {
                // version 0: a zero is an overlong pulse; version 1: it is followed by a 24-bit cycle count
                if (_version >= 1 && i + 3 < _pulses.Count) { cycles = _pulses[i + 1] | _pulses[i + 2] << 8 | _pulses[i + 3] << 16; i += 3; }
                else cycles = 256 * 8;
            }
            sb.Append(cycles <= ShortLimit ? 'S' : cycles <= MediumLimit ? 'M' : cycles <= GapLimit ? 'L' : 'G');
        }
        return sb.ToString();
    }

    /// <summary>One byte: the marker L,M, eight data bits least significant first, then an odd-parity bit.</summary>
    static bool TryByte(string p, ref int i, out byte value)
    {
        value = 0;
        if (i + 20 > p.Length || p[i] != 'L' || p[i + 1] != 'M') return false;
        int bits = 0, ones = 0;
        for (int k = 0; k < 9; k++)
        {
            char a = p[i + 2 + 2 * k], b = p[i + 3 + 2 * k];
            int bit = a == 'S' && b == 'M' ? 0 : a == 'M' && b == 'S' ? 1 : -1;
            if (bit < 0) return false;
            if (k < 8) bits |= bit << k;
            ones += bit;
        }
        if (ones % 2 == 0) return false; // the nine bits (parity included) must contain an odd number of ones
        value = (byte)bits;
        i += 20;
        return true;
    }

    sealed record Block(bool Second, byte[] Data, bool Valid);

    static List<Block> Blocks(string p)
    {
        var blocks = new List<Block>();
        int i = 0;
        while (i < p.Length)
        {
            var bytes = new List<byte>();
            int j = i;
            while (TryByte(p, ref j, out byte b)) bytes.Add(b);
            if (bytes.Count == 0) { i++; continue; }
            if (j + 1 < p.Length && p[j] == 'L' && p[j + 1] == 'S') j += 2; // end-of-data marker

            // a block starts with the countdown $89..$81 (first copy) or $09..$01 (repeat), ends with an XOR checksum
            if (bytes.Count >= 10 && (bytes[0] == 0x89 || bytes[0] == 0x09))
            {
                bool second = bytes[0] == 0x09;
                bool countdown = Enumerable.Range(0, 9).All(k => bytes[k] == (byte)(bytes[0] - k));
                var data = bytes.GetRange(9, bytes.Count - 10).ToArray();
                byte check = 0;
                foreach (byte b in data) check ^= b;
                blocks.Add(new Block(second, data, countdown && check == bytes[^1]));
            }
            i = j;
        }
        return blocks;
    }

    void Decode()
    {
        _items.Clear();
        // keep one good copy of every block: the first if its checksum is right, otherwise the repeat that follows it
        var chosen = new List<byte[]>();
        var blocks = Blocks(Classify());
        for (int i = 0; i < blocks.Count; i++)
        {
            var b = blocks[i];
            if (b.Second) continue;
            var repeat = i + 1 < blocks.Count && blocks[i + 1].Second ? blocks[i + 1] : null;
            if (b.Valid) chosen.Add(b.Data);
            else if (repeat is { Valid: true }) chosen.Add(repeat.Data);
        }

        for (int i = 0; i + 1 < chosen.Count; i++)
        {
            var h = chosen[i];
            if (h.Length < 21 || (h[0] != 1 && h[0] != 3)) continue; // only program headers (BASIC or machine code)
            int start = h[1] | h[2] << 8, end = h[3] | h[4] << 8;
            var data = chosen[i + 1];
            int length = Math.Min(data.Length, Math.Max(0, end - start));
            var prg = new byte[length + 2];
            prg[0] = (byte)start; prg[1] = (byte)(start >> 8);
            Array.Copy(data, 0, prg, 2, length);
            _items.Add(new Item { Name = DosText.Decode(h.AsSpan(5, 16)).TrimEnd(' ', '\0'), Prg = prg });
            i++;
        }
    }

    // ---------- encoding ----------
    void Pulse(int length, int count = 1)
    {
        for (int i = 0; i < count; i++) _pulses.Add((byte)length);
    }

    void Bit(int value)
    {
        if (value == 0) { Pulse(ShortPulse); Pulse(MediumPulse); }
        else { Pulse(MediumPulse); Pulse(ShortPulse); }
    }

    void Byte(byte value)
    {
        Pulse(LongPulse); Pulse(MediumPulse); // the byte marker
        int ones = 0;
        for (int k = 0; k < 8; k++) { int bit = value >> k & 1; ones += bit; Bit(bit); }
        Bit(ones % 2 == 0 ? 1 : 0);           // parity makes the number of ones odd
    }

    void Gap(int cycles)
    {
        if (_version >= 1) { _pulses.Add(0); _pulses.Add((byte)cycles); _pulses.Add((byte)(cycles >> 8)); _pulses.Add((byte)(cycles >> 16)); }
        else _pulses.Add(0);
    }

    /// <summary>Records a block twice: leader, countdown, data, checksum and end marker, the second time with the repeat countdown.</summary>
    void Record(byte[] data, int leader)
    {
        byte check = 0;
        foreach (byte b in data) check ^= b;
        for (int copy = 0; copy < 2; copy++)
        {
            Pulse(ShortPulse, copy == 0 ? leader : 79);
            for (int k = 0; k < 9; k++) Byte((byte)((copy == 0 ? 0x89 : 0x09) - k));
            foreach (byte b in data) Byte(b);
            Byte(check);
            Pulse(LongPulse); Pulse(ShortPulse); // end of data
        }
        Pulse(ShortPulse, 4);
    }

    // ---------- IDiskDrive ----------
    public IReadOnlyList<DirectoryEntry> Directory() =>
        _items.Select(i => new DirectoryEntry(i.Name, FileType.Prg, Math.Max(1, (i.Prg.Length - 2 + 253) / 254))).ToList();

    public DriveFile Read(string pattern, FileType? type = null)
    {
        if (type is not (null or FileType.Prg)) throw new DriveException(62);
        // an empty name means "the next program on the tape"; a name picks the newest program called that
        var item = pattern.Length == 0 || pattern == "*"
            ? _items.FirstOrDefault()
            : _items.LastOrDefault(i => DosText.Matches(pattern, i.Name));
        return item == null ? throw new DriveException(62) : new DriveFile(item.Name, FileType.Prg, item.Prg);
    }

    public void Write(string name, FileType type, byte[] data, bool replace)
    {
        if (type != FileType.Prg) throw new DriveException(64);
        DosText.ValidateName(name);
        if (!replace && _items.Any(i => i.Name == name)) throw new DriveException(63);
        if (data.Length < 2) throw new DriveException(30);

        int start = data[0] | data[1] << 8, length = data.Length - 2;
        var header = new byte[192];
        Array.Fill(header, (byte)0x20);
        header[0] = (byte)(start == 0x0801 ? 1 : 3); // relocatable BASIC program, or a machine code file at a fixed address
        header[1] = (byte)start; header[2] = (byte)(start >> 8);
        int end = start + length;
        header[3] = (byte)end; header[4] = (byte)(end >> 8);
        for (int i = 0; i < 16; i++) header[5 + i] = i < name.Length ? DosText.ToByte(name[i]) : (byte)0x20;

        Record(header, 4096);
        Gap(100_000);
        Record(data[2..], 1500);
        Gap(100_000);

        _items.Add(new Item { Name = name, Prg = data });
        Changed();
    }

    public int Scratch(string pattern) => throw new DriveException(26);   // a tape cannot be edited in place

    public void Rename(string from, string to) => throw new DriveException(26);

    /// <summary>Erases the tape.</summary>
    public void Format(string name, string? id)
    {
        _pulses.Clear();
        _items.Clear();
        Title = name;
        Changed();
    }

    public void Validate() { }

    public byte[] ToArray()
    {
        var image = new byte[HeaderSize + _pulses.Count];
        Encoding.ASCII.GetBytes(Signature).CopyTo(image, 0);
        image[12] = _version;
        BitConverter.GetBytes(_pulses.Count).CopyTo(image, 16);
        _pulses.CopyTo(image, HeaderSize);
        return image;
    }

    void Changed() => _save?.Invoke(ToArray());
}
