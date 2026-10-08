using C64Basic.Core.Disk;

namespace C64Basic.Gui;

/// <summary>Opens disk and tape image files, creating blank ones, and writes every change back.</summary>
static class ImageFiles
{
    public static D64Image OpenDisk(string path)
    {
        Action<byte[]> save = data => File.WriteAllBytes(path, data);
        if (File.Exists(path)) return new D64Image(File.ReadAllBytes(path), save);
        var blank = D64Image.Create(Path.GetFileNameWithoutExtension(path).ToUpperInvariant(), "00", save);
        save(blank.ToArray());
        return blank;
    }

    /// <summary>A <c>.tap</c> pulse image or, for any other name, a <c>.t64</c> archive.</summary>
    public static IDiskDrive OpenTape(string path)
    {
        Action<byte[]> save = data => File.WriteAllBytes(path, data);
        string title = Path.GetFileNameWithoutExtension(path).ToUpperInvariant();
        bool exists = File.Exists(path);
        byte[]? bytes = exists ? File.ReadAllBytes(path) : null;
        if (path.EndsWith(".tap", StringComparison.OrdinalIgnoreCase))
        {
            var tap = new TapImage(bytes, title, save);
            if (!exists) save(tap.ToArray());
            return tap;
        }
        var t64 = new T64Image(bytes, title, save);
        if (!exists) save(t64.ToArray());
        return t64;
    }
}
