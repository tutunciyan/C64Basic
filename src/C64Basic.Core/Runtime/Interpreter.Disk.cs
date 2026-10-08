using System.Text;
using C64Basic.Core.Disk;
using C64Basic.Core.Machine;
using C64Basic.Core.Parsing;

namespace C64Basic.Core.Runtime;

/// <summary>Drives, LOAD/SAVE/VERIFY, directory listings and the drive's error channel.</summary>
public sealed partial class Interpreter
{
    readonly Dictionary<int, IDiskDrive> _drives = new();
    readonly Dictionary<int, DriveStatus> _driveStatus = new();
    bool _quietDisk;

    /// <summary>Attaches a drive (a mounted <c>.d64</c>, a tape image, ...) as a device number.</summary>
    public void MountDrive(int device, IDiskDrive drive)
    {
        _drives[device] = drive;
        _driveStatus[device] = DriveStatus.Ok;
    }

    /// <summary>The drive on a device: a mounted one, or the host directory for tape (1) and disk (8-11).</summary>
    public IDiskDrive DriveFor(int device)
    {
        if (_drives.TryGetValue(device, out var drive)) return drive;
        if (device == 1) drive = new HostDirectoryDrive(_fs, "TAPE");
        else if (device is >= 8 and <= 11) drive = new HostDirectoryDrive(_fs);
        else throw new BasicException(ErrorCode.DeviceNotPresent);
        return _drives[device] = drive;
    }

    /// <summary>The drive for LOAD, SAVE and VERIFY: the keyboard and screen cannot hold programs.</summary>
    IDiskDrive StorageDrive(int device)
    {
        if (device is 0 or 3) throw new BasicException(ErrorCode.IllegalDevice);
        return DriveFor(device);
    }

    DriveStatus StatusOf(int device) => _driveStatus.GetValueOrDefault(device, DriveStatus.Ok);

    void SetStatus(int device, DriveStatus status) => _driveStatus[device] = status;

    /// <summary>Loads a program without the SEARCHING/LOADING messages, as the command line does.</summary>
    public void LoadQuietly(string path)
    {
        _quietDisk = true;
        try { ProcessLine($"LOAD \"{path}\""); }
        finally { _quietDisk = false; }
    }

    /// <summary>Splits "@0:NAME" into the file name and whether the file may replace an existing one.</summary>
    static (string Name, bool Replace) ParseSpec(string spec)
    {
        spec = spec.Trim();
        bool replace = spec.StartsWith('@');
        if (replace) spec = spec[1..];
        return (DosCommands.StripDriveNumber(spec), replace);
    }

    string NameOf(Expr? e)
    {
        if (e == null) return "";
        var v = Eval(e);
        if (!v.IsStr) throw new BasicException(ErrorCode.TypeMismatch);
        return v.S!.Trim();
    }

    // ---------- LOAD / SAVE / VERIFY ----------
    void Announce(string text)
    {
        if (_curLine >= 0 || _quietDisk) return; // BASIC only prints these in direct mode
        NewLineIfNeeded();
        Write(text + "\n");
    }

    (string Name, int Device, int Secondary) ResolveFileArgs(FileArgs a, bool nameRequired)
    {
        string name = NameOf(a.Name);
        // a real C64 defaults to the tape (device 1); outside strict mode the disk is more useful
        int device = a.Device != null ? ToInt(Eval(a.Device), 0, 255) : _opts.Strict ? 1 : 8;
        int secondary = a.Secondary != null ? ToInt(Eval(a.Secondary), 0, 255) : 0;
        if (name.Length == 0 && nameRequired && device != 1) throw new BasicException(ErrorCode.MissingFileName);
        return (name, device, secondary);
    }

    void DoLoad(FileArgs args, bool verify)
    {
        var (name, device, secondary) = ResolveFileArgs(args, nameRequired: true);
        var drive = StorageDrive(device);
        var (path, _) = ParseSpec(name);

        if (device == 1) Announce("PRESS PLAY ON TAPE\nOK");
        Announce(path.Length > 0 ? $"SEARCHING FOR {path}" : "SEARCHING");
        try
        {
            if (path.StartsWith('$') && !verify)
            {
                if (device == 1) throw new DriveException(62);
                ReplaceProgram(DirectoryListing(drive, DirectoryPattern(path)), keepOrder: true);
                SetStatus(device, DriveStatus.Ok);
                Announce("LOADING");
                return;
            }

            var text = secondary == 0 ? drive.ReadProgramText(path) : null;
            DriveFile? file = text == null ? drive.Read(path) : null;
            if (device == 1) Announce($"FOUND {file?.Name ?? path}");
            Announce(verify ? "VERIFYING" : "LOADING");
            SetStatus(device, DriveStatus.Ok);

            if (verify) { VerifyAgainst(text, file, secondary); Announce("OK"); return; }
            if (text != null) ReplaceProgram(ParseListing(text));
            else if (secondary == 0) ReplaceProgram(PrgFormat.Detokenize(file!.Data));
            else LoadToMemory(file!.Data, null); // ",8,1": at the file's own address; the program keeps running
        }
        catch (DriveException e)
        {
            SetStatus(device, DriveStatus.Of(e.Code, e.Track, e.Sector));
            if (e.Code == 62) throw new BasicException(ErrorCode.FileNotFound);
            // other DOS errors only show on the drive's error channel
        }
    }

    static string DirectoryPattern(string path)
    {
        int colon = path.IndexOf(':');
        return colon >= 0 ? path[(colon + 1)..] : "*";
    }

    void VerifyAgainst(string[]? text, DriveFile? file, int secondary)
    {
        if (text != null)
        {
            var saved = text.Where(l => l.Trim().Length > 0).Select(l => l.Trim()).ToList();
            if (!saved.SequenceEqual(Listing())) throw new BasicException(ErrorCode.Verify);
            return;
        }
        var data = file!.Data;
        if (secondary == 0)
        {
            var current = PrgFormat.Tokenize(_lines.Select(l => (l.Number, l.Text)));
            if (!data.AsSpan().SequenceEqual(current)) throw new BasicException(ErrorCode.Verify);
            return;
        }
        int address = PrgFormat.LoadAddress(data);
        for (int i = 2; i < data.Length && address + i - 2 < 65536; i++)
            if (_bus.Ram[address + i - 2] != data[i]) throw new BasicException(ErrorCode.Verify);
    }

    void DoSave(FileArgs args)
    {
        var (name, device, _) = ResolveFileArgs(args, nameRequired: true);
        var drive = StorageDrive(device);
        var (path, replace) = ParseSpec(name);

        if (device == 1) Announce("PRESS RECORD & PLAY ON TAPE\nOK");
        Announce(path.Length > 0 ? $"SAVING {path}" : "SAVING");
        try
        {
            var prg = PrgFormat.Tokenize(_lines.Select(l => (l.Number, l.Text)));
            // a host directory has no "file exists" error: SAVE simply overwrites, as it always did here
            drive.WriteProgram(path, prg, Listing().ToList(), replace || drive is HostDirectoryDrive);
            SetStatus(device, DriveStatus.Ok);
        }
        catch (DriveException e)
        {
            SetStatus(device, DriveStatus.Of(e.Code, e.Track, e.Sector));
        }
    }

    /// <summary>
    /// Replaces the program with these lines; like LOAD on a real C64 it ends whatever was running. With
    /// <paramref name="keepOrder"/> the lines are kept exactly as given, so a directory can list several files
    /// with the same block count.
    /// </summary>
    void ReplaceProgram(IEnumerable<(int Number, string Text)> lines, bool keepOrder = false)
    {
        _lines.Clear();
        ClearState();
        foreach (var (number, text) in lines)
        {
            if (number > MaxLineNumber) throw new BasicException(ErrorCode.Load);
            if (keepOrder) _lines.Add(new ProgramLine { Number = number, Text = text });
            else StoreLine(number, _lexer.Normalize(text));
        }
        ProgramChanged();
        _halted = true;
    }

    static IEnumerable<(int Number, string Text)> ParseListing(IEnumerable<string> listing)
    {
        foreach (var raw in listing)
        {
            string line = raw.TrimStart();
            if (line.Length == 0 || !char.IsAsciiDigit(line[0])) continue;
            int j = 0;
            while (j < line.Length && char.IsAsciiDigit(line[j])) j++;
            if (!int.TryParse(line.AsSpan(0, j), out int number)) throw new BasicException(ErrorCode.Load);
            yield return (number, line[j..].TrimStart(' ').TrimEnd());
        }
    }

    // ---------- memory ----------
    /// <summary>Stores a PRG's data at <paramref name="address"/> (or its own load address); returns the end address + 1.</summary>
    int LoadToMemory(byte[] prg, int? address)
    {
        int start = address ?? PrgFormat.LoadAddress(prg);
        int count = Math.Min(prg.Length - 2, 65536 - start);
        for (int i = 0; i < count; i++)
        {
            int a = start + i;
            // I/O, screen and the CPU port go through the bus so the chips and the screen see them
            if (a < 0x100 || (a >= 1024 && a < 2024) || a >= 0xD000) _bus.Write(a, prg[2 + i]);
            else _bus.Ram[a] = prg[2 + i];
        }
        return start + Math.Max(0, count);
    }

    // ---------- directory listing ----------
    /// <summary>The directory as a BASIC program, like <c>LOAD "$",8</c> produces.</summary>
    static List<(int Number, string Text)> DirectoryListing(IDiskDrive drive, string pattern)
    {
        var lines = new List<(int, string)>
        {
            (0, $"\"\u0012{drive.Title.PadRight(16)[..16]}\" {drive.Id.PadRight(2)[..2]} 2A"),
        };
        foreach (var e in drive.Directory().Where(e => DosText.Matches(pattern, e.Name)))
        {
            var sb = new StringBuilder();
            sb.Append(' ', Math.Max(0, 4 - e.Blocks.ToString().Length));
            sb.Append('"').Append(e.Name).Append('"').Append(' ', 17 - e.Name.Length - (e.Closed ? 0 : 1));
            if (!e.Closed) sb.Append('*');
            sb.Append(e.Type.ToString().ToUpperInvariant());
            if (e.Locked) sb.Append('<');
            lines.Add((e.Blocks, sb.ToString()));
        }
        lines.Add((drive.BlocksFree, "BLOCKS FREE."));
        return lines;
    }

    // ---------- command channel ----------
    void RunDriveCommand(BasicFile f, string command)
    {
        var status = DosCommands.Execute(DriveFor(f.Device), command);
        SetStatus(f.Device, status);
        f.Text = status + "\r";
        f.Pos = 0;
    }

    /// <summary>Text written to channel 15 is collected until a CR, then run as a DOS command.</summary>
    void CommandInput(BasicFile f, string s)
    {
        f.Buffer.Append(s);
        string all = f.Buffer.ToString();
        int cr;
        while ((cr = all.IndexOf('\r')) >= 0)
        {
            RunDriveCommand(f, all[..cr]);
            all = all[(cr + 1)..];
        }
        f.Buffer.Clear().Append(all);
    }

    // ---------- KERNAL LOAD and SAVE ----------
    string KernalFileName()
    {
        int length = _bus.Ram[0xB7], pointer = _bus.Ram[0xBB] | _bus.Ram[0xBC] << 8;
        var bytes = new byte[length];
        for (int i = 0; i < length; i++) bytes[i] = _bus.Ram[(pointer + i) & 0xFFFF];
        return DosText.Decode(bytes);
    }

    /// <summary>KERNAL error numbers returned in A with the carry flag set.</summary>
    const byte KernalFileNotFound = 4, KernalDeviceNotPresent = 5, KernalMissingName = 8;

    TrapResult KernalLoad(Cpu6502 c)
    {
        int device = _bus.Ram[0xBA], secondary = _bus.Ram[0xB9];
        bool verify = c.A != 0;
        c.SetFlag(Cpu6502.FlagC, true);
        try
        {
            var (path, _) = ParseSpec(KernalFileName());
            if (path.Length == 0 && device != 1) { c.A = KernalMissingName; return TrapResult.Return; }
            var drive = DriveFor(device);

            byte[] data;
            if (path.StartsWith('$'))
                data = PrgFormat.Tokenize(DirectoryListing(drive, DirectoryPattern(path)), secondary == 0 ? c.X | c.Y << 8 : 0x0401);
            else data = drive.Read(path).Data;

            int start = secondary == 0 ? c.X | c.Y << 8 : PrgFormat.LoadAddress(data);
            if (verify)
            {
                for (int i = 2; i < data.Length; i++)
                    if (_bus.Ram[(start + i - 2) & 0xFFFF] != data[i]) { c.A = 0; return TrapResult.Return; }
                c.SetFlag(Cpu6502.FlagC, false);
                return TrapResult.Return;
            }
            int end = LoadToMemory(data, start);
            c.X = (byte)end; c.Y = (byte)(end >> 8);
            c.SetFlag(Cpu6502.FlagC, false);
            SetStatus(device, DriveStatus.Ok);
        }
        catch (BasicException) { c.A = KernalDeviceNotPresent; }
        catch (DriveException e)
        {
            SetStatus(device, DriveStatus.Of(e.Code, e.Track, e.Sector));
            c.A = e.Code == 62 ? KernalFileNotFound : KernalMissingName;
        }
        return TrapResult.Return;
    }

    TrapResult KernalSave(Cpu6502 c)
    {
        int device = _bus.Ram[0xBA];
        c.SetFlag(Cpu6502.FlagC, true);
        try
        {
            var (path, replace) = ParseSpec(KernalFileName());
            if (path.Length == 0 && device != 1) { c.A = KernalMissingName; return TrapResult.Return; }
            var drive = DriveFor(device);

            int start = _bus.Ram[c.A] | _bus.Ram[(c.A + 1) & 0xFF] << 8, end = c.X | c.Y << 8;
            int count = Math.Max(0, end - start);
            var prg = new byte[count + 2];
            prg[0] = (byte)start; prg[1] = (byte)(start >> 8);
            for (int i = 0; i < count; i++) prg[2 + i] = _bus.Ram[(start + i) & 0xFFFF];

            drive.Write(path, FileType.Prg, prg, replace || drive is HostDirectoryDrive);
            c.SetFlag(Cpu6502.FlagC, false);
            SetStatus(device, DriveStatus.Ok);
        }
        catch (BasicException) { c.A = KernalDeviceNotPresent; }
        catch (DriveException e)
        {
            SetStatus(device, DriveStatus.Of(e.Code, e.Track, e.Sector));
            c.A = KernalMissingName;
        }
        return TrapResult.Return;
    }
}
