using System.Collections.Concurrent;
using System.Text;
using C64Basic.Core.Parsing;

namespace C64Basic.Core.Runtime;

/// <summary>What a loaded machine state was doing when it was saved.</summary>
/// <param name="WasRunning">The BASIC program was running (not waiting at the READY. prompt).</param>
/// <param name="NeedsContinue">The program was running but this interpreter is at the prompt: type CONT to carry on.</param>
public readonly record struct StateLoadResult(bool WasRunning, bool NeedsContinue);

/// <summary>
/// Saving and restoring the whole machine: memory and chips, the BASIC program, variables, arrays, DEF FN functions, FOR and GOSUB
/// stacks, the DATA pointer and the clock. Files that were open are closed and machine code that was mid-SYS cannot be saved
/// (the request waits until it returns). The built-in character ROM and the mounted drives are configuration, not state.
/// </summary>
public sealed partial class Interpreter
{
    const string StateMagic = "C64BAS";
    const int StateVersion = 2;   // 2 adds the CIA timer outputs, shift register and CNT level; version 1 files still load

    readonly ConcurrentQueue<Action> _safePoints = new();
    readonly Dictionary<string, int> _fnSites = new(); // where each DEF FN ran: an index into the program
    bool _executing;

    // ---------- doing it from another thread ----------
    /// <summary>Saves the machine at the next safe point (between BASIC statements or at the READY. prompt) and passes the bytes on.</summary>
    public void SaveStateLater(Action<byte[]> done) => _safePoints.Enqueue(() => done(SaveState()));

    /// <summary>Restores a saved machine at the next safe point. <paramref name="done"/> gets the result or the error.</summary>
    public void LoadStateLater(byte[] data, Action<StateLoadResult?, Exception?> done) => _safePoints.Enqueue(() =>
    {
        try { done(LoadState(data), null); }
        catch (Exception e) when (e is InvalidDataException or EndOfStreamException or BasicException) { done(null, e); }
    });

    /// <summary>Runs what other threads asked for. Called between statements of a running program and while waiting for a key.</summary>
    public void RunPendingRequests()
    {
        while (_safePoints.TryDequeue(out var action)) action();
    }

    // ---------- saving ----------
    public byte[] SaveState()
    {
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
        {
            w.Write(Encoding.ASCII.GetBytes(StateMagic));
            w.Write(StateVersion);

            bool running = _executing && _curLine >= 0;
            w.Write(running);
            w.Write(_curLine); w.Write(_curStmt);
            w.Write(_unordered);

            w.Write(_lines.Count);
            foreach (var l in _lines) { w.Write(l.Number); w.Write(l.Text); }

            w.Write(_vars.Count);
            foreach (var (key, value) in _vars) { w.Write(key); WriteValue(w, value); }

            w.Write(_arrays.Count);
            foreach (var (key, array) in _arrays)
            {
                w.Write(key);
                w.Write(array.Bounds.Length);
                foreach (int b in array.Bounds) w.Write(b);
                foreach (var v in array.Data) WriteValue(w, v);
            }

            var sites = _fns.Keys.Where(_fnSites.ContainsKey).ToList();
            w.Write(sites.Count);
            foreach (var key in sites) { w.Write(key); w.Write(_fnSites[key]); }

            w.Write(_forStack.Count);
            foreach (var f in _forStack) { w.Write(f.Key); w.Write(f.Limit); w.Write(f.Step); w.Write(f.Line); w.Write(f.Stmt); }
            w.Write(_gosubStack.Count);
            foreach (var (line, stmt) in _gosubStack) { w.Write(line); w.Write(stmt); }

            w.Write(_dataPos);
            w.Write(_trace);
            w.Write(_st);
            w.Write(Col);
            w.Write(NowSeconds());

            _bus.SaveState(w);
        }
        return ms.ToArray();
    }

    static void WriteValue(BinaryWriter w, Value v)
    {
        w.Write(v.IsStr);
        if (v.IsStr) w.Write(v.S!); else w.Write(v.N);
    }

    static Value ReadValue(BinaryReader r) => r.ReadBoolean() ? Value.Str(r.ReadString()) : Value.Num(r.ReadDouble());

    // ---------- loading ----------
    /// <summary>
    /// Replaces the machine with a saved one. Everything is read before anything changes, so a damaged file leaves the machine
    /// as it was.
    /// </summary>
    public StateLoadResult LoadState(byte[] data)
    {
        using var ms = new MemoryStream(data);
        using var r = new BinaryReader(ms, Encoding.UTF8);
        if (data.Length < StateMagic.Length + 4 || Encoding.ASCII.GetString(r.ReadBytes(StateMagic.Length)) != StateMagic)
            throw new InvalidDataException("not a saved machine state");
        int version = r.ReadInt32();
        if (version is < 1 or > StateVersion) throw new InvalidDataException($"unsupported state version {version}");

        bool wasRunning = r.ReadBoolean();
        int curLine = r.ReadInt32(), curStmt = r.ReadInt32();
        bool unordered = r.ReadBoolean();

        var lines = new List<ProgramLine>();
        for (int i = 0, n = Count(r); i < n; i++) lines.Add(new ProgramLine { Number = r.ReadInt32(), Text = r.ReadString() });

        var vars = new Dictionary<string, Value>();
        for (int i = 0, n = Count(r); i < n; i++) { string key = r.ReadString(); vars[key] = ReadValue(r); }

        var arrays = new Dictionary<string, BasicArray>();
        for (int i = 0, n = Count(r); i < n; i++)
        {
            string key = r.ReadString();
            var bounds = new int[Count(r)];
            for (int b = 0; b < bounds.Length; b++) bounds[b] = r.ReadInt32();
            var array = new BasicArray(bounds, Value.Zero);
            for (int d = 0; d < array.Data.Length; d++) array.Data[d] = ReadValue(r);
            arrays[key] = array;
        }

        var sites = new Dictionary<string, int>();
        for (int i = 0, n = Count(r); i < n; i++) { string key = r.ReadString(); sites[key] = r.ReadInt32(); }

        var forStack = new List<ForFrame>();
        for (int i = 0, n = Count(r); i < n; i++)
            forStack.Add(new ForFrame { Key = r.ReadString(), Limit = r.ReadDouble(), Step = r.ReadDouble(), Line = r.ReadInt32(), Stmt = r.ReadInt32() });
        var gosubStack = new List<(int, int)>();
        for (int i = 0, n = Count(r); i < n; i++) gosubStack.Add((r.ReadInt32(), r.ReadInt32()));

        int dataPos = r.ReadInt32();
        bool trace = r.ReadBoolean();
        int st = r.ReadInt32(), col = r.ReadInt32();
        double clock = r.ReadDouble();

        // read the machine part into a scratch machine first: if the file is damaged this throws before anything has changed
        long machineStart = ms.Position;
        new Machine.Bus().LoadState(r, version);
        ms.Position = machineStart;

        // ---- apply ----
        _lines.Clear();
        _lines.AddRange(lines);
        _unordered = unordered;
        _vars = vars;
        _arrays = arrays;
        _fns = new();
        _fnSites.Clear();
        foreach (var (key, line) in sites)
        {
            if (line < 0 || line >= _lines.Count) continue;
            var def = StmtsOf(_lines[line]).OfType<DefFnStmt>().FirstOrDefault(d => d.Key == key);
            if (def == null) continue;
            _fns[key] = (def.ParamKey, def.Body);
            _fnSites[key] = line;
        }
        _forStack.Clear(); _forStack.AddRange(forStack);
        _gosubStack.Clear(); _gosubStack.AddRange(gosubStack);
        _dataPos = dataPos;
        _data = null; // rebuilt from the program on demand
        _trace = trace;
        _st = st;
        Col = col;
        _clockOffsetSeconds = clock - _bus.Seconds();

        _files.Clear();
        _cmdFile = null;
        _kernalInput = null;
        _pendingKey = "";
        _chrinLine = "";
        _chrinPos = 0;
        ProgramChanged();

        _bus.LoadState(r, version);

        // where execution goes next
        bool atPrompt = !_executing || _curLine < 0;
        if (wasRunning)
        {
            _curLine = curLine; _curStmt = curStmt;
            _halted = false;
            _canCont = atPrompt;            // at the prompt the host types CONT; in a running program it simply goes on
            _contLine = curLine; _contStmt = curStmt;
        }
        else
        {
            _halted = true;                 // a running program stops at the prompt
            _canCont = false;
            if (_executing) _curLine = -1;
        }
        return new StateLoadResult(wasRunning, wasRunning && atPrompt);
    }

    /// <summary>A count read from the file, which must be plausible so a damaged file cannot ask for gigabytes.</summary>
    static int Count(BinaryReader r)
    {
        int n = r.ReadInt32();
        if (n < 0 || n > 1_000_000) throw new InvalidDataException("damaged state: bad count");
        return n;
    }
}
