using System.Diagnostics;
using C64Basic.Core.IO;
using C64Basic.Core.Lexing;
using C64Basic.Core.Machine;
using C64Basic.Core.Parsing;

namespace C64Basic.Core.Runtime;

public sealed class InterpreterOptions
{
    /// <summary>Disable every extension: only stock BASIC V2 keywords, 80-character lines, no excerpts in errors.</summary>
    public bool Strict { get; init; }

    /// <summary>Raise ?OVERFLOW above ~1.7E38 and flush values below ~2.9E-39 to zero, like the 40-bit MBF format.</summary>
    public bool EmulateMbfRange { get; init; } = true;

    /// <summary>Slows execution to about this many statements per second so animations run at C64 speed. 0 = full speed.</summary>
    public int StatementsPerSecond { get; init; }
}

sealed class ProgramLine
{
    public int Number;
    public string Text = "";
    public List<Stmt>? Stmts;
}

sealed record DataItem(string Text, bool Quoted);

sealed class ForFrame
{
    public string Key = "";
    public double Limit, Step;
    public int Line, Stmt;
}

/// <summary>
/// A Commodore 64 BASIC V2 interpreter. Feed it lines with <see cref="ProcessLine"/>, exactly as if typed at
/// the READY. prompt: numbered lines edit the program, anything else runs immediately.
/// </summary>
public sealed partial class Interpreter
{
    const int MaxLineNumber = 63999;
    const int BasicBytesFree = 38911;

    readonly IConsoleDevice _dev;
    readonly IFileSystem _fs;
    readonly InterpreterOptions _opts;
    readonly Lexer _lexer;

    readonly List<ProgramLine> _lines = new();
    Dictionary<string, Value> _vars = new();
    Dictionary<string, BasicArray> _arrays = new();
    Dictionary<string, (string Param, Expr Body)> _fns = new();
    readonly List<ForFrame> _forStack = new();
    readonly List<(int Line, int Stmt)> _gosubStack = new();
    readonly Bus _bus = new();

    /// <summary>The memory map PEEK and POKE go through; front ends watch it for hardware writes.</summary>
    public Bus Bus => _bus;

    int Peek(int address)
    {
        if (address >= 160 && address <= 162) SyncJiffies();
        return _bus.Read(address);
    }

    /// <summary>The jiffy clock at 160-162 (high byte first) reads the same time as TI.</summary>
    void SyncJiffies()
    {
        long jiffies = (long)Math.Floor(NowSeconds() * 60);
        _bus.Ram[160] = (byte)(jiffies >> 16);
        _bus.Ram[161] = (byte)(jiffies >> 8);
        _bus.Ram[162] = (byte)jiffies;
    }
    double _clockOffsetSeconds;
    Random _rng = new();

    // execution state: _curLine == -1 means "the direct-mode line"
    List<Stmt> _directStmts = new();
    int _curLine = -1;
    int _curStmt;
    bool _halted;
    bool _canCont;
    int _contLine, _contStmt;
    bool _trace;
    int _fnDepth;

    List<DataItem>? _data;
    int _dataPos;

    /// <summary>Current cursor column; PRINT uses it for TAB, SPC and comma zones.</summary>
    public int Col { get; private set; }

    public bool AutoActive { get; private set; }
    public int AutoNext { get; private set; }
    public int AutoStep { get; private set; }

    public Interpreter(IConsoleDevice device, IFileSystem fileSystem, InterpreterOptions? options = null)
    {
        _dev = device;
        _fs = fileSystem;
        _opts = options ?? new InterpreterOptions();
        _lexer = new Lexer(_opts.Strict);
        _dev.Attach(_bus);
    }

    public bool Strict => _opts.Strict;
    public int LineCount => _lines.Count;
    public void ResetColumn() => Col = 0;
    public void CancelAuto() => AutoActive = false;

    // ---------- output ----------
    public void Write(string s)
    {
        if (s.Length == 0) return;
        if (_cmdFile != null) { RedirectedWrite(s); return; }
        _dev.Write(s);
        foreach (char c in s)
        {
            switch (c)
            {
                case '\n': case '\r': case '\u0093': case '\u0013': Col = 0; break;
                case '\u001d': Col++; break;
                case '\u009d': if (Col > 0) Col--; break;
                default: if (c >= ' ' && !(c >= '\u0080' && c <= '\u009f')) Col++; break;
            }
        }
    }

    void NewLineIfNeeded()
    {
        if (Col != 0) Write("\n");
    }

    // ---------- program store ----------
    /// <summary>
    /// Set after LOAD "$": a directory listing has several lines with the same number and out of order. Lookups then scan
    /// from the top for the first line at or after the number, which is what the real line editor does.
    /// </summary>
    bool _unordered;

    int FindLine(int number)
    {
        if (_unordered)
        {
            bool ascending = true;
            for (int i = 1; i < _lines.Count && ascending; i++) ascending = _lines[i - 1].Number < _lines[i].Number;
            if (ascending) _unordered = false;
            else
            {
                for (int i = 0; i < _lines.Count; i++)
                    if (_lines[i].Number >= number) return _lines[i].Number == number ? i : ~i;
                return ~_lines.Count;
            }
        }
        int lo = 0, hi = _lines.Count - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            int cmp = _lines[mid].Number.CompareTo(number);
            if (cmp == 0) return mid;
            if (cmp < 0) lo = mid + 1; else hi = mid - 1;
        }
        return ~lo;
    }

    void StoreLine(int number, string text)
    {
        int idx = FindLine(number);
        if (text.Length == 0)
        {
            if (idx >= 0) _lines.RemoveAt(idx);
        }
        else if (idx >= 0)
        {
            _lines[idx].Text = text;
            _lines[idx].Stmts = null;
        }
        else _lines.Insert(~idx, new ProgramLine { Number = number, Text = text });
        ProgramChanged();
    }

    void ProgramChanged()
    {
        _canCont = false;
        _data = null;
    }

    List<Stmt> StmtsOf(ProgramLine line) => line.Stmts ??= Parser.ParseLine(line.Text, _lexer);

    string LineListing(ProgramLine l) => $"{l.Number} {l.Text}";

    public IEnumerable<string> Listing() => _lines.Select(LineListing);

    // ---------- entering lines ----------
    /// <summary>
    /// Handles one line typed at the prompt. Returns true if a direct-mode command ran, which is when
    /// the caller should print READY.
    /// </summary>
    public bool ProcessLine(string input)
    {
        int i = 0;
        while (i < input.Length && input[i] == ' ') i++;
        string s = input[i..].TrimEnd('\r', '\n', ' ');
        if (s.Length == 0) return false;

        if (char.IsAsciiDigit(s[0]))
        {
            int j = 0;
            while (j < s.Length && char.IsAsciiDigit(s[j])) j++;
            _curLine = -1;
            try
            {
                if (!int.TryParse(s.AsSpan(0, j), out int number) || number > MaxLineNumber)
                    throw new BasicException(ErrorCode.Syntax);
                string body = _lexer.Normalize(s[j..].TrimStart(' '));
                if (body.Length > (_opts.Strict ? 80 : 255)) throw new BasicException(ErrorCode.StringTooLong);
                StoreLine(number, body);
                return false;
            }
            catch (BasicException e)
            {
                ReportError(e);
                return true;
            }
        }

        _directStmts = Parser.ParseLine(s, _lexer);
        _curLine = -1;
        _curStmt = 0;
        LastRunFailed = false;
        RunLoop();
        return true;
    }

    /// <summary>True if the most recent direct-mode line ended with an error.</summary>
    public bool LastRunFailed { get; private set; }

    // ---------- the run loop ----------
    void RunLoop()
    {
        try { Execute(); }
        catch (BasicException e)
        {
            _canCont = false;
            LastRunFailed = true;
            ReportError(e);
        }
    }

    void Execute()
    {
        _halted = false;
        while (!_halted)
        {
            if (_dev.BreakRequested)
            {
                _dev.BreakRequested = false;
                BreakHere();
                return;
            }

            List<Stmt> stmts;
            if (_curLine < 0) stmts = _directStmts;
            else if (_curLine >= _lines.Count) { _canCont = false; return; }
            else stmts = StmtsOf(_lines[_curLine]);

            if (_curStmt >= stmts.Count)
            {
                if (_curLine < 0) return;
                _curLine++;
                _curStmt = 0;
                continue;
            }

            if (_opts.StatementsPerSecond > 0 && !Warp) Throttle();

            if (_trace && _curStmt == 0 && _curLine >= 0) Write($"[{_lines[_curLine].Number}]");
            Exec(stmts[_curStmt++]);
        }
    }

    /// <summary>Runs at full speed even though the options ask for C64 speed; a host can toggle it while running.</summary>
    public volatile bool Warp;

    long _throttleStart, _throttleCount;

    /// <summary>Every 32 statements, sleeps if the program is running ahead of the configured statement rate.</summary>
    void Throttle()
    {
        if (_throttleCount == 0) _throttleStart = Stopwatch.GetTimestamp();
        if ((++_throttleCount & 31) != 0) return;
        double expected = _throttleCount / (double)_opts.StatementsPerSecond;
        double actual = Stopwatch.GetElapsedTime(_throttleStart).TotalSeconds;
        if (expected - actual > 0.002) Thread.Sleep((int)((expected - actual) * 1000));
        if (actual > expected + 0.5) _throttleCount = 0; // we fell behind (e.g. waiting for input): restart the clock
    }

    void BreakHere()
    {
        NewLineIfNeeded();
        Write(_curLine >= 0 && _curLine < _lines.Count ? $"BREAK IN {_lines[_curLine].Number}\n" : "BREAK\n");
        _canCont = _curLine >= 0;
        _contLine = _curLine;
        _contStmt = _curStmt;
    }

    void ReportError(BasicException e)
    {
        _cmdFile = null;
        NewLineIfNeeded();
        int? ln = _curLine >= 0 && _curLine < _lines.Count ? _lines[_curLine].Number : null;
        Write($"?{ErrorNames.Name(e.Code)}  ERROR" + (ln != null ? $" IN {ln}" : "") + "\n");
        if (!_opts.Strict && ln != null && e.Column >= 0)
        {
            string prefix = $"{ln} ";
            Write(prefix + _lines[_curLine].Text + "\n");
            Write(new string(' ', prefix.Length + e.Column) + "^\n");
        }
    }

    // ---------- control flow helpers ----------
    void JumpTo(int line)
    {
        int idx = FindLine(line);
        if (idx < 0) throw new BasicException(ErrorCode.UndefdStatement);
        _curLine = idx;
        _curStmt = 0;
    }

    void ClearState()
    {
        _vars = new();
        _arrays = new();
        _fns = new();
        _forStack.Clear();
        _gosubStack.Clear();
        _dataPos = 0;
        _data = null;
        _files.Clear();
        _cmdFile = null;
    }

    // ---------- statements ----------
    void Exec(Stmt s)
    {
        switch (s)
        {
            case ErrorStmt e:
                throw new BasicException(e.Code, e.Column);
            case NopStmt:
            case DataStmt:
                break;
            case LetStmt l:
                Assign(l.Target, Eval(l.Value));
                break;
            case PrintStmt p:
                DoPrint(p);
                break;
            case IfStmt i:
                if (!IsTrue(Eval(i.Cond))) _curStmt = i.ElseIdx >= 0 ? i.ElseIdx : int.MaxValue;
                break;
            case JumpEndStmt:
                _curStmt = int.MaxValue;
                break;
            case GotoStmt g:
                JumpTo(g.Line);
                break;
            case GosubStmt g:
                {
                    var ret = (_curLine, _curStmt);
                    JumpTo(g.Line);
                    _gosubStack.Add(ret);
                    break;
                }
            case ReturnStmt:
                {
                    if (_gosubStack.Count == 0) throw new BasicException(ErrorCode.ReturnWithoutGosub);
                    (_curLine, _curStmt) = _gosubStack[^1];
                    _gosubStack.RemoveAt(_gosubStack.Count - 1);
                    break;
                }
            case OnStmt o:
                DoOn(o);
                break;
            case ForStmt f:
                DoFor(f);
                break;
            case NextStmt n:
                DoNext(n);
                break;
            case EndStmt:
                _halted = true;
                _canCont = _curLine >= 0;
                _contLine = _curLine;
                _contStmt = _curStmt;
                break;
            case StopStmt:
                BreakHere();
                _halted = true;
                break;
            case ReadStmt r:
                foreach (var t in r.Targets) DoRead(t);
                break;
            case RestoreStmt:
                _dataPos = 0;
                break;
            case InputStmt inp:
                DoInput(inp);
                break;
            case GetStmt g:
                {
                    if (_curLine < 0) throw new BasicException(ErrorCode.IllegalDirect);
                    AssignKey(g.Target, TakeKey());
                    break;
                }
            case DimStmt d:
                foreach (var item in d.Items) DoDim(item);
                break;
            case DefFnStmt d:
                if (_curLine < 0) throw new BasicException(ErrorCode.IllegalDirect);
                _fns[d.Key] = (d.ParamKey, d.Body);
                break;
            case PokeStmt p:
                {
                    int addr = ToInt(Eval(p.Address), 0, 65535);
                    int val = ToInt(Eval(p.Value), 0, 255);
                    _bus.Write(addr, (byte)val);
                    if (addr >= 160 && addr <= 162)
                        _clockOffsetSeconds = (_bus.Ram[160] << 16 | _bus.Ram[161] << 8 | _bus.Ram[162]) / 60.0 - _bus.Seconds();
                    if (addr == 211) Col = Math.Min(val, 79);
                    break;
                }
            case GetFileStmt gf: DoGetFile(gf); break;
            case InputFileStmt inf: DoInputFile(inf); break;
            case OpenStmt o: DoOpen(o); break;
            case CloseStmt c: DoClose(c.File); break;
            case CmdStmt cmd: DoCmd(cmd); break;
            case WaitStmt w: DoWait(w); break;
            case SysStmt sy: DoSys(sy); break;
            default:
                ExecCommand(s);
                break;
        }
    }

    static bool IsTrue(Value v)
    {
        if (v.IsStr) throw new BasicException(ErrorCode.TypeMismatch);
        return v.N != 0;
    }

    void DoPrint(PrintStmt p)
    {
        BasicFile? file = null;
        if (p.File != null)
        {
            file = FileFrom(p.File);
            if (!file.Writing) throw new BasicException(ErrorCode.NotOutputFile);
            if (_cmdFile == file) _cmdFile = null;
        }
        void Out(string t) { if (file == null) Write(t); else FileOut(file, t); }
        int ColNow() => file?.Col ?? Col;
        bool lastWasSep = false;
        foreach (var part in p.Parts)
        {
            if (part.E == null)
            {
                lastWasSep = true;
                if (part.Sep == ',') Out(new string(' ', 10 - ColNow() % 10));
                continue;
            }
            lastWasSep = false;

            if (part.E is FuncCall { Name: "TAB" or "SPC" } call)
            {
                int n = ToInt(Eval(call.Args[0]), 0, 255);
                int pad = call.Name == "TAB" ? n - ColNow() : n;
                if (pad > 0) Out(new string(' ', pad));
                continue;
            }

            var v = Eval(part.E);
            Out(v.IsStr ? v.S! : NumberFormat.Format(v.N) + " ");
        }
        if (!lastWasSep) Out(file == null ? "\n" : "\r");
    }

    void DoOn(OnStmt o)
    {
        int k = ToInt(Eval(o.Selector), 0, 255);
        if (k == 0 || k > o.Targets.Length) return;
        int target = o.Targets[k - 1];
        if (o.Gosub)
        {
            var ret = (_curLine, _curStmt);
            JumpTo(target);
            _gosubStack.Add(ret);
        }
        else JumpTo(target);
    }

    void DoFor(ForStmt f)
    {
        var start = Eval(f.From);
        var limit = EvalNum(f.To);
        double step = f.Step == null ? 1 : EvalNum(f.Step);
        Assign(f.Var, start);

        int existing = _forStack.FindIndex(x => x.Key == f.Var.Key);
        if (existing >= 0) _forStack.RemoveRange(existing, _forStack.Count - existing);
        _forStack.Add(new ForFrame { Key = f.Var.Key, Limit = limit, Step = step, Line = _curLine, Stmt = _curStmt });
    }

    void DoNext(NextStmt n)
    {
        var keys = n.Keys.Length == 0 ? new string?[] { null } : n.Keys.Cast<string?>().ToArray();
        foreach (var key in keys)
        {
            int idx = key == null ? _forStack.Count - 1 : _forStack.FindLastIndex(x => x.Key == key);
            if (idx < 0) throw new BasicException(ErrorCode.NextWithoutFor);
            _forStack.RemoveRange(idx + 1, _forStack.Count - idx - 1);
            var frame = _forStack[idx];

            var type = frame.Key.EndsWith('%') ? VarType.Int : VarType.Real;
            double v = Check(GetVar(frame.Key, type).N + frame.Step);
            SetVar(frame.Key, type, Value.Num(v));

            bool done = frame.Step >= 0 ? v > frame.Limit : v < frame.Limit;
            if (done)
            {
                _forStack.RemoveAt(idx);
                continue;
            }
            _curLine = frame.Line;
            _curStmt = frame.Stmt;
            return;
        }
    }

    void DoDim(DimItem item)
    {
        if (_arrays.ContainsKey(item.Key)) throw new BasicException(ErrorCode.RedimdArray);
        var bounds = item.Dims.Select(d => ToInt(Eval(d), 0, 32767)).ToArray();
        long elements = 1;
        foreach (var b in bounds) elements *= b + 1;
        int bytesEach = item.Type == VarType.Real ? 5 : item.Type == VarType.Int ? 2 : 3;
        if (elements * bytesEach > BasicBytesFree) throw new BasicException(ErrorCode.OutOfMemory);
        _arrays[item.Key] = new BasicArray(bounds, DefaultFor(item.Type));
    }

    // ---------- DATA / READ / INPUT ----------
    static List<DataItem> SplitItems(string raw)
    {
        var items = new List<DataItem>();
        int i = 0, n = raw.Length;
        while (true)
        {
            while (i < n && raw[i] == ' ') i++;
            if (i < n && raw[i] == '"')
            {
                int close = raw.IndexOf('"', i + 1);
                if (close < 0) close = n;
                items.Add(new DataItem(raw.Substring(i + 1, close - i - 1), true));
                int comma = raw.IndexOf(',', Math.Min(close + 1, n));
                i = comma < 0 ? n : comma;
            }
            else
            {
                int comma = raw.IndexOf(',', i);
                if (comma < 0) comma = n;
                items.Add(new DataItem(raw[i..comma].Trim(), false));
                i = comma;
            }
            if (i >= n) break;
            i++; // skip the comma
        }
        return items;
    }

    void EnsureData()
    {
        if (_data != null) return;
        _data = new();
        foreach (var line in _lines)
            foreach (var st in StmtsOf(line))
                if (st is DataStmt d) _data.AddRange(SplitItems(d.Raw));
    }

    void DoRead(Expr target)
    {
        EnsureData();
        if (_dataPos >= _data!.Count) throw new BasicException(ErrorCode.OutOfData);
        var item = _data[_dataPos++];
        bool isStr = target is VarRef { Type: VarType.Str } or ArrayRef { Type: VarType.Str };
        if (isStr) Assign(target, Value.Str(item.Text));
        else if (item.Text.Length == 0) Assign(target, Value.Zero);
        else if (!item.Quoted && NumberParser.TryParseFull(item.Text, out double d)) Assign(target, Value.Num(Check(d)));
        else throw new BasicException(ErrorCode.Syntax);
    }

    void DoInput(InputStmt s)
    {
        if (_curLine < 0) throw new BasicException(ErrorCode.IllegalDirect);
        while (true)
        {
            Write((s.Prompt ?? "") + "? ");
            var items = new List<DataItem>();
            while (items.Count < s.Targets.Length)
            {
                if (items.Count > 0) Write("?? ");
                string? line = _dev.ReadLine();
                Col = 0;
                if (line == null) throw new InputEndedException();
                items.AddRange(SplitItems(line));
            }

            bool ok = true;
            for (int i = 0; i < s.Targets.Length && ok; i++)
            {
                var target = s.Targets[i];
                bool isStr = target is VarRef { Type: VarType.Str } or ArrayRef { Type: VarType.Str };
                if (isStr) Assign(target, Value.Str(items[i].Text));
                else if (items[i].Text.Length == 0) continue;
                else if (NumberParser.TryParseFull(items[i].Text, out double d)) Assign(target, Value.Num(Check(d)));
                else ok = false;
            }
            if (ok)
            {
                if (items.Count > s.Targets.Length) Write("?EXTRA IGNORED\n");
                return;
            }
            Write("?REDO FROM START\n");
        }
    }
}
