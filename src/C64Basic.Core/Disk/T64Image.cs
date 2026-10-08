using System.Text;

namespace C64Basic.Core.Disk;

/// <summary>
/// A T64 tape archive: a list of named programs. Reading and writing rebuild the container, which keeps it simple;
/// only PRG-type entries (what a datasette holds) are supported. Changes are passed to <c>save</c>.
/// </summary>
public sealed class T64Image : IDiskDrive
{
    const int HeaderSize = 64, EntrySize = 32, MinimumEntries = 30;
    const string Signature = "C64S tape image file";

    sealed class Item
    {
        public string Name = "";
        public byte[] Prg = Array.Empty<byte>(); // load address + data, as for a disk
    }

    readonly List<Item> _items = new();
    readonly Action<byte[]>? _save;

    public string Title { get; private set; }
    public string Id => "";
    public int BlocksFree => Math.Max(0, 664 - _items.Sum(BlocksOf));

    public T64Image(byte[]? image = null, string title = "TAPE", Action<byte[]>? save = null)
    {
        _save = save;
        Title = title;
        if (image == null) return;
        if (image.Length < HeaderSize || Encoding.ASCII.GetString(image, 0, 3) != "C64")
            throw new InvalidDataException("not a T64 tape image");

        Title = Encoding.ASCII.GetString(image, 0x28, 24).TrimEnd(' ', '\0');
        int used = image[0x24] | image[0x25] << 8, max = image[0x22] | image[0x23] << 8;
        for (int i = 0; i < max && _items.Count < used; i++)
        {
            int e = HeaderSize + i * EntrySize;
            if (e + EntrySize > image.Length) break;
            if (image[e] != 1) continue; // free slot
            int start = image[e + 2] | image[e + 3] << 8, end = image[e + 4] | image[e + 5] << 8;
            int offset = image[e + 8] | image[e + 9] << 8 | image[e + 10] << 16 | image[e + 11] << 24;
            int length = Math.Max(0, Math.Min(end - start, image.Length - offset));
            var prg = new byte[length + 2];
            prg[0] = (byte)start; prg[1] = (byte)(start >> 8);
            Array.Copy(image, offset, prg, 2, length);
            _items.Add(new Item { Name = Encoding.ASCII.GetString(image, e + 16, 16).TrimEnd(' ', '\0'), Prg = prg });
        }
    }

    static int BlocksOf(Item i) => Math.Max(1, (i.Prg.Length - 2 + 253) / 254);

    public IReadOnlyList<DirectoryEntry> Directory() =>
        _items.Select(i => new DirectoryEntry(i.Name, FileType.Prg, BlocksOf(i))).ToList();

    public DriveFile Read(string pattern, FileType? type = null)
    {
        if (type is not (null or FileType.Prg)) throw new DriveException(62);
        // an empty name means "the next program on the tape"
        var item = pattern.Length == 0 || pattern == "*"
            ? _items.FirstOrDefault()
            : _items.FirstOrDefault(i => DosText.Matches(pattern, i.Name));
        return item == null ? throw new DriveException(62) : new DriveFile(item.Name, FileType.Prg, item.Prg);
    }

    public void Write(string name, FileType type, byte[] data, bool replace)
    {
        if (type != FileType.Prg) throw new DriveException(64);
        DosText.ValidateName(name);
        if (replace) _items.RemoveAll(i => i.Name == name);
        if (_items.Any(i => i.Name == name)) throw new DriveException(63);
        _items.Add(new Item { Name = name, Prg = data });
        Changed();
    }

    public int Scratch(string pattern)
    {
        int n = _items.RemoveAll(i => DosText.Matches(pattern, i.Name));
        if (n > 0) Changed();
        return n;
    }

    public void Rename(string from, string to)
    {
        var item = _items.FirstOrDefault(i => DosText.Matches(from, i.Name)) ?? throw new DriveException(62);
        item.Name = to;
        Changed();
    }

    public void Format(string name, string? id)
    {
        _items.Clear();
        Title = name;
        Changed();
    }

    public void Validate() { }

    public byte[] ToArray()
    {
        int entries = Math.Max(MinimumEntries, _items.Count);
        int dataStart = HeaderSize + entries * EntrySize;
        var output = new List<byte>(new byte[dataStart]);
        for (int i = 0; i < Signature.Length; i++) output[i] = (byte)Signature[i];
        output[0x20] = 0; output[0x21] = 1;
        output[0x22] = (byte)entries; output[0x23] = (byte)(entries >> 8);
        output[0x24] = (byte)_items.Count; output[0x25] = (byte)(_items.Count >> 8);
        var title = Encoding.ASCII.GetBytes(Title.PadRight(24)[..24]);
        for (int i = 0; i < 24; i++) output[0x28 + i] = title[i];

        int offset = dataStart;
        for (int i = 0; i < _items.Count; i++)
        {
            var prg = _items[i].Prg;
            int start = prg.Length >= 2 ? prg[0] | prg[1] << 8 : 0, length = Math.Max(0, prg.Length - 2);
            int e = HeaderSize + i * EntrySize;
            output[e] = 1; output[e + 1] = 0x82;
            output[e + 2] = (byte)start; output[e + 3] = (byte)(start >> 8);
            int end = start + length;
            output[e + 4] = (byte)end; output[e + 5] = (byte)(end >> 8);
            for (int b = 0; b < 4; b++) output[e + 8 + b] = (byte)(offset >> 8 * b);
            var name = Encoding.ASCII.GetBytes(_items[i].Name.PadRight(16)[..16]);
            for (int b = 0; b < 16; b++) output[e + 16 + b] = name[b];
            output.AddRange(prg.Skip(2));
            offset += length;
        }
        return output.ToArray();
    }

    void Changed() => _save?.Invoke(ToArray());
}
