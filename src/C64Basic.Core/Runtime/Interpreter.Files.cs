using System.Text;
using C64Basic.Core.Disk;
using C64Basic.Core.Parsing;

namespace C64Basic.Core.Runtime;

enum FileKind { Disk, Keyboard, Screen, Command, Printer }

sealed class BasicFile
{
    public int Number;
    public int Device;
    public int Secondary;
    /// <summary>The 256-byte buffer of a direct-access channel (OPEN n,8,n,"#"); <see cref="Pos"/> is its pointer.</summary>
    public byte[]? Block;

    // relative files: the file, the current record and byte (0-based), the record being read, and the bytes written so far
    public IRelFile? Rel;
    public int RelRecord, RelPos;
    public byte[]? RelData;
    public readonly List<byte> RelOut = new();
    public bool RelPendingCr;

    /// <summary>A position command was just run: the CR that PRINT# sends after it is not an (empty) command.</summary>
    public bool SkipCommandCr;
    public FileKind Kind;
    public string Path = "";
    public bool Writing, Append;
    /// <summary>The text is raw bytes from M-R, not a status line.</summary>
    public bool Raw;
    /// <summary>The drive refused the OPEN (60, 63): the channel stays but reads nothing and writes nowhere.</summary>
    public bool Failed;
    public string Text = "";
    public int Pos;
    public readonly StringBuilder Buffer = new();
    public int Col;

    public void Put(string s)
    {
        Buffer.Append(s);
        int reset = s.LastIndexOf('\r');
        Col = reset >= 0 ? s.Length - reset - 1 : Col + s.Length;
    }
}

public sealed partial class Interpreter
{
    const int MaxOpenFiles = 10;

    readonly Dictionary<int, BasicFile> _files = new();
    BasicFile? _cmdFile;
    int _st;
    string _pendingKey = "";

    BasicFile FileFrom(Expr e)
    {
        int n = ToInt(Eval(e), 0, 255);
        if (!_files.TryGetValue(n, out var f)) throw new BasicException(ErrorCode.FileNotOpen);
        return f;
    }

    void DoOpen(OpenStmt o)
    {
        int lf = ToInt(Eval(o.Args[0]), 1, 255);
        int dev = o.Args.Length > 1 ? ToInt(Eval(o.Args[1]), 0, 255) : 1;
        int sa = o.Args.Length > 2 ? ToInt(Eval(o.Args[2]), 0, 255) : 0;
        string name = "";
        if (o.Args.Length > 3)
        {
            var v = Eval(o.Args[3]);
            if (!v.IsStr) throw new BasicException(ErrorCode.TypeMismatch);
            name = v.S!;
        }
        OpenFile(lf, dev, sa, name);
    }

    /// <summary>OPEN for BASIC and for the KERNAL's OPEN call.</summary>
    void OpenFile(int lf, int dev, int sa, string name)
    {
        if (_files.ContainsKey(lf)) throw new BasicException(ErrorCode.FileOpen);
        if (_files.Count >= MaxOpenFiles) throw new BasicException(ErrorCode.TooManyFiles);

        var f = new BasicFile { Number = lf };
        switch (dev)
        {
            case 0: f.Kind = FileKind.Keyboard; break;
            case 3: f.Kind = FileKind.Screen; f.Writing = true; break;
            case 4 or 5: f.Kind = FileKind.Printer; f.Writing = true; break;
            case 1 or (>= 8 and <= 11):
                f.Device = dev;
                if (sa == 15)
                {
                    f.Kind = FileKind.Command;
                    f.Writing = true; // the command channel is both: PRINT# sends commands, INPUT# reads the status
                    f.Text = StatusOf(dev) + "\r";
                    if (name.Length > 0) RunDriveCommand(f, name); // OPEN 15,8,15,"S:OLD" runs it at once
                }
                else if (name.StartsWith('#'))
                {
                    f.Block = new byte[256];
                    f.Writing = true; // like the command channel, a buffer is both read and written
                }
                else OpenDisk(f, name, sa);
                f.Secondary = sa;
                break;
            default:
                throw new BasicException(ErrorCode.DeviceNotPresent);
        }
        _st = 0;
        _files[lf] = f;
    }

    void OpenDisk(BasicFile f, string name, int sa)
    {
        string spec = name.Trim();
        if (spec.StartsWith('@')) spec = spec[1..];
        if (spec.Length >= 2 && spec[1] == ':' && char.IsAsciiDigit(spec[0])) spec = spec[2..];
        // the record length is a raw character after ",L,", so this spec must not be trimmed
        string raw = name.TrimStart().TrimStart('@');
        if (raw.Length >= 2 && raw[1] == ':' && char.IsAsciiDigit(raw[0])) raw = raw[2..];
        if (TryOpenRel(f, raw, sa)) return;
        var parts = spec.Split(',');
        string path = parts[0].Trim();
        if (path.Length == 0) throw new BasicException(ErrorCode.MissingFileName);

        bool? write = sa == 1 ? true : sa == 0 ? false : null;
        FileType? type = null;
        foreach (var p in parts.Skip(1))
        {
            switch (p.Trim().ToUpperInvariant())
            {
                case "W": write = true; break;
                case "A": write = true; f.Append = true; break;
                case "R": write = false; break;
                case "S": type = FileType.Seq; break;
                case "P": type = FileType.Prg; break;
                case "U": type = FileType.Usr; break;
            }
        }
        f.Path = path;
        f.Writing = write ?? false;
        bool replace = name.TrimStart().StartsWith('@');
        var drive = DriveFor(f.Device);
        try
        {
            // a file that is still being written cannot be opened again: its directory entry is not complete
            if (_files.Values.Any(o => o.Kind == FileKind.Disk && o.Writing && o.Device == f.Device && o.Rel == null && o.Block == null
                                       && string.Equals(o.Path, path, StringComparison.OrdinalIgnoreCase)))
                throw new DriveException(60);
            if (f.Writing)
            {
                bool exists = SafeExists(drive, path);
                if (f.Append && exists) f.Buffer.Append(drive.ReadText(path));
                // the host directory has always overwritten; a disk image refuses unless the name starts with @
                else if (exists && !replace && drive is not HostDirectoryDrive) throw new DriveException(63);
            }
            else
            {
                var file = drive.Read(path);
                if (type != null && file.Type != type) throw new DriveException(64);
                f.Text = DosText.Decode(file.Data);
            }
            SetStatus(f.Device, DriveStatus.Ok);
        }
        catch (DriveException e)
        {
            SetStatus(f.Device, DriveStatus.Of(e.Code, e.Track, e.Sector));
            if (e.Code == 62 && !f.Writing) throw new BasicException(ErrorCode.FileNotFound);
            if (e.Code is 60 or 63) f.Failed = true;   // nothing was opened on the drive: closing must not write the file
        }
    }

    static bool SafeExists(IDiskDrive drive, string path)
    {
        try { drive.ReadText(path); return true; }
        catch (DriveException) { return false; }
    }

    void DoClose(Expr e) => CloseFile(ToInt(Eval(e), 0, 255));

    void CloseFile(int n)
    {
        if (!_files.Remove(n, out var f)) return; // closing a closed file is not an error
        if (_cmdFile == f) _cmdFile = null;
        if (_kernalInput == f) _kernalInput = null;
        CommitRelRecord(f, terminated: false);
        switch (f.Kind)
        {
            case FileKind.Disk when f.Writing && f.Rel == null && f.Block == null && !f.Failed:
                try
                {
                    DriveFor(f.Device).WriteText(f.Path, f.Buffer.ToString(), replace: true);
                    SetStatus(f.Device, DriveStatus.Ok);
                }
                catch (DriveException ex) { SetStatus(f.Device, DriveStatus.Of(ex.Code, ex.Track, ex.Sector)); }
                break;
            case FileKind.Command when f.Buffer.Length > 0:
                RunDriveCommand(f, f.Buffer.ToString());
                break;
            case FileKind.Printer:
                {
                    const string PrinterFile = "PRINTER.TXT";
                    string old = _fs.Exists(PrinterFile) ? _fs.ReadAllText(PrinterFile) : "";
                    _fs.WriteAllText(PrinterFile, old + f.Buffer);
                    break;
                }
        }
    }

    void DoCmd(CmdStmt c)
    {
        var f = FileFrom(c.File);
        if (!f.Writing) throw new BasicException(ErrorCode.NotOutputFile);
        _cmdFile = f.Kind == FileKind.Screen ? null : f;
        if (c.Text != null)
        {
            var v = Eval(c.Text);
            if (!v.IsStr) throw new BasicException(ErrorCode.TypeMismatch);
            Write(v.S!);
        }
    }

    /// <summary>Output for PRINT#: goes to the file's buffer, or the screen for device 3.</summary>
    void FileOut(BasicFile f, string s)
    {
        if (f.Kind == FileKind.Screen) { Write(s.Replace('\r', '\n')); return; }
        if (f.Kind == FileKind.Command) { CommandInput(f, s); return; }
        if (f.Rel != null) { RelWrite(f, s); return; }
        if (f.Block != null)
        {
            foreach (char c in s)
                if (f.Pos < 256) f.Block[f.Pos++] = DosText.ToByte(c); // bytes go in at the pointer; past the end they are lost
            return;
        }
        f.Put(s);
    }

    void RedirectedWrite(string s) => FileOut(_cmdFile!, s.Replace('\n', '\r'));

    // ---------- reading ----------
    int ReadChar(BasicFile f, bool refill)
    {
        if (f.Pos >= f.Text.Length && f.Kind == FileKind.Keyboard && refill)
        {
            string? line = _dev.ReadLine();
            if (line == null) throw new InputEndedException();
            f.Text = line + "\r";
            f.Pos = 0;
        }
        if (f.Rel != null) return RelReadChar(f);
        if (f.Block != null)
        {
            if (f.Pos >= 256) { _st = 64; return -1; }
            char ch = Petscii.ToChar(f.Block[f.Pos++]);
            if (f.Pos >= 256) _st = 64;
            return ch;
        }
        // the error channel reports the drive's current status, which may have changed since OPEN
        if (f.Kind == FileKind.Command && f.Pos == 0 && !f.Raw) f.Text = StatusOf(f.Device) + "\r";
        if (f.Kind == FileKind.Command && f.Pos >= f.Text.Length)
        {
            f.Raw = false;
            // the status was read: the channel goes back to reporting "00, OK"
            SetStatus(f.Device, DriveStatus.Ok);
            f.Text = DriveStatus.Ok + "\r";
            f.Pos = 0;
        }
        if (f.Pos >= f.Text.Length) { _st = 64; return -1; }
        char c = f.Text[f.Pos++];
        if (f.Kind != FileKind.Keyboard && f.Pos >= f.Text.Length) _st = 64;
        return c;
    }

    static void RequireInput(BasicFile f)
    {
        if (f.Writing && f.Kind != FileKind.Command && f.Block == null && f.Rel == null) throw new BasicException(ErrorCode.NotInputFile);
    }

    static bool IsStrTarget(Expr t) => t is VarRef { Type: VarType.Str } or ArrayRef { Type: VarType.Str };

    void DoInputFile(InputFileStmt s)
    {
        var f = FileFrom(s.File);
        RequireInput(f);
        foreach (var t in s.Targets)
        {
            string item = ReadItem(f);
            if (IsStrTarget(t)) Assign(t, Value.Str(item));
            else if (item.Length == 0) Assign(t, Value.Zero);
            else if (NumberParser.TryParseFull(item, out double d)) Assign(t, Value.Num(Check(d)));
            else throw new BasicException(ErrorCode.FileData);
        }
    }

    /// <summary>Reads one item: quoted text, or text up to a comma, colon or CR.</summary>
    string ReadItem(BasicFile f)
    {
        var sb = new StringBuilder();
        int c = ReadChar(f, true);
        while (c == ' ') c = ReadChar(f, true);
        if (c == '"')
        {
            while ((c = ReadChar(f, true)) != -1 && c != '"' && c != '\r') sb.Append((char)c);
            if (c == '"')
            {
                while ((c = ReadChar(f, true)) == ' ') { }
                if (c != ',' && c != ':' && c != '\r' && c != -1) f.Pos--;
            }
            return sb.ToString();
        }
        while (c != -1 && c != ',' && c != ':' && c != '\r')
        {
            sb.Append((char)c);
            c = ReadChar(f, true);
        }
        return sb.ToString().TrimEnd(' ');
    }

    void DoGetFile(GetFileStmt g)
    {
        var f = FileFrom(g.File);
        RequireInput(f);
        string key;
        if (f.Kind == FileKind.Keyboard) key = TakeKey();
        else
        {
            int c = ReadChar(f, false);
            key = c < 0 ? "" : ((char)c).ToString();
        }
        AssignKey(g.Target, key);
    }

    void AssignKey(Expr target, string key) =>
        Assign(target, IsStrTarget(target)
            ? Value.Str(key)
            : Value.Num(key.Length > 0 && char.IsAsciiDigit(key[0]) ? key[0] - '0' : 0));

    /// <summary>The key waiting from WAIT 198 if any, otherwise a non-blocking read from the device.</summary>
    string TakeKey()
    {
        if (_pendingKey.Length > 0)
        {
            string k = _pendingKey;
            _pendingKey = "";
            _bus.Ram[198] = 0;
            return k;
        }
        return _dev.GetKey();
    }

    // ---------- WAIT ----------
    void DoWait(WaitStmt w)
    {
        int addr = ToInt(Eval(w.Address), 0, 65535);
        int mask = ToInt(Eval(w.Mask), 0, 255);
        int xor = w.Xor == null ? 0 : ToInt(Eval(w.Xor), 0, 255);
        while (true)
        {
            if (addr == 198 && _pendingKey.Length == 0)
            {
                string k = _dev.GetKey();
                if (k.Length > 0) { _pendingKey = k; _bus.Ram[198] = 1; }
            }
            if (((Peek(addr) ^ xor) & mask) != 0) return;
            if (_dev.BreakRequested)
            {
                _curStmt--; // CONT retries the WAIT
                return;
            }
            Thread.Sleep(1);
        }
    }

    public string Banner =>
        $"\n{(_opts.Strict ? "    **** COMMODORE 64 BASIC V2" : "   **** COMMODORE 64 BASIC V2 (C#)")} ****\n\n 64K RAM SYSTEM  38911 BASIC BYTES FREE\n\nREADY.\n";
}
