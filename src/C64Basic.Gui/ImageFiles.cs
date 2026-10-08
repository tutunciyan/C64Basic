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

    public static T64Image OpenTape(string path)
    {
        Action<byte[]> save = data => File.WriteAllBytes(path, data);
        if (File.Exists(path)) return new T64Image(File.ReadAllBytes(path), save: save);
        var blank = new T64Image(null, Path.GetFileNameWithoutExtension(path).ToUpperInvariant(), save);
        save(blank.ToArray());
        return blank;
    }
}
