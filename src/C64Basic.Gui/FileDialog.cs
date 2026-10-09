using System.Diagnostics;
using System.Runtime.InteropServices;

namespace C64Basic.Gui;

/// <summary>
/// The system's "open file" dialog, asked for in the way each platform offers it (SDL2 has none): the common dialog on Windows,
/// <c>zenity</c> or <c>kdialog</c> on Linux, AppleScript on macOS. It blocks, so call it from a thread of its own.
/// </summary>
static class FileDialog
{
    public const string Extensions = "d64 g64 t64 prg tap crt sav bas";

    static string _lastFolder = "";

    /// <summary>The file picked, or null when the dialog was cancelled. <paramref name="problem"/> says why it could not be shown.</summary>
    public static string? Open(string title, out string? problem)
    {
        problem = null;
        string? path;
        try
        {
            if (OperatingSystem.IsWindows()) path = OpenWindows(title);
            else if (OperatingSystem.IsMacOS()) path = Run("osascript", new[] { "-e", "POSIX path of (choose file with prompt \"" + title + "\")" });
            else path = Linux(title, out problem);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            problem = "no file dialog available: " + e.Message;
            return null;
        }
        if (path != null) _lastFolder = Path.GetDirectoryName(path) ?? _lastFolder;
        return path;
    }

    static string? Linux(string title, out string? problem)
    {
        problem = null;
        string patterns = string.Join(" ", Extensions.Split(' ').Select(e => "*." + e));
        foreach (var (tool, args) in new[]
        {
            ("zenity", new[] { "--file-selection", "--title=" + title, "--file-filter=C64 files | " + patterns + " | " + patterns.ToUpperInvariant(), "--file-filter=All files | *", "--filename=" + (_lastFolder.Length > 0 ? _lastFolder + "/" : "") }),
            ("kdialog", new[] { "--getopenfilename", _lastFolder.Length > 0 ? _lastFolder : ".", patterns + "|C64 files", "--title", title }),
        })
        {
            try { return Run(tool, args); }
            catch (System.ComponentModel.Win32Exception) { }                  // not installed: try the next
        }
        problem = "no file dialog: install zenity or kdialog, or drop the file on the window";
        return null;
    }

    static string? Run(string tool, string[] args)
    {
        var info = new ProcessStartInfo(tool) { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (string a in args) info.ArgumentList.Add(a);
        using var process = Process.Start(info) ?? throw new InvalidOperationException(tool + " did not start");
        string output = process.StandardOutput.ReadToEnd().Trim();
        process.WaitForExit();
        return process.ExitCode == 0 && output.Length > 0 && File.Exists(output) ? output : null;
    }

    // ---------- Windows: GetOpenFileNameW ----------
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct OpenFileName
    {
        public int StructSize;
        public nint Owner, Instance;
        public string? Filter, CustomFilter;
        public int MaxCustomFilter, FilterIndex;
        public nint File;
        public int MaxFile;
        public nint FileTitle;
        public int MaxFileTitle;
        public string? InitialDir, Title;
        public int Flags;
        public short FileOffset, FileExtension;
        public string? DefaultExtension;
        public nint CustomData, Hook;
        public string? TemplateName;
        public nint Reserved;
        public int ReservedFlags, FlagsEx;
    }

    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetOpenFileNameW")]
    static extern bool GetOpenFileName(ref OpenFileName ofn);

    static string? OpenWindows(string title)
    {
        const int PathMustExist = 0x800, FileMustExist = 0x1000, NoChangeDir = 0x8, Explorer = 0x80000, MaxPath = 1024;
        string patterns = string.Join(";", Extensions.Split(' ').Select(e => "*." + e));
        nint buffer = Marshal.AllocHGlobal(MaxPath * 2);
        try
        {
            Marshal.WriteInt16(buffer, 0);
            var ofn = new OpenFileName
            {
                StructSize = Marshal.SizeOf<OpenFileName>(),
                Filter = "C64 files (" + patterns + ")\0" + patterns + "\0All files\0*.*\0\0",
                FilterIndex = 1,
                File = buffer,
                MaxFile = MaxPath,
                Title = title,
                InitialDir = _lastFolder.Length > 0 ? _lastFolder : null,
                Flags = PathMustExist | FileMustExist | NoChangeDir | Explorer,
            };
            return GetOpenFileName(ref ofn) ? Marshal.PtrToStringUni(buffer) : null;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
}
