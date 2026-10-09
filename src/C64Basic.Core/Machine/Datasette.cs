using C64Basic.Core.Disk;

namespace C64Basic.Core.Machine;

/// <summary>
/// A cassette recorder on the C64's cassette port, for ROM mode: with PLAY down and the motor on (processor port bit 5 low) it feeds the
/// pulses of a <see cref="TapImage"/> to CIA 1's FLAG pin, one falling edge per pulse at the right moment, so the real KERNAL tape
/// routines (and turbo loaders) read them. PLAY also makes bit 4 of the processor port read low (the sense switch). With recording on,
/// the rising edges of the write line (bit 3, where the real KERNAL puts the flux reversal of each pulse) are timed and added to the tape when the motor stops. Time is the C64's own cycle count.
/// </summary>
public sealed class Datasette
{
    int[] _pulses = Array.Empty<int>();
    int _next;                       // the pulse whose end is the next edge
    long _edgeAt;                    // the cycle of that edge while the tape runs
    long _remaining;                 // cycles left to it while the motor is off
    bool _running;
    readonly List<int> _recorded = new();
    long _lastFall = -1;
    bool _writeLevel = true;

    /// <summary>The tape in the recorder, or null.</summary>
    public TapImage? Tape { get; private set; }

    /// <summary>PLAY (or RECORD and PLAY) is down: the sense switch reads closed and a running motor moves the tape.</summary>
    public bool Play { get; set; }

    /// <summary>Pulses written to the cassette write line are added to the tape.</summary>
    public bool Recording { get; set; }

    /// <summary>True while a tape is in the recorder.</summary>
    public bool Active => Tape != null;

    /// <summary>The pulse the tape is at (0 = the start), and how many there are.</summary>
    public int Position => _next;
    public int Length => _pulses.Length;

    public bool AtEnd => _next >= _pulses.Length;

    public void Insert(TapImage? tape, bool record = true)
    {
        Tape = tape;
        _pulses = tape?.PulseCycles() ?? Array.Empty<int>();
        Rewind();
        Play = tape != null;
        Recording = tape != null && record;
        _recorded.Clear();
        _lastFall = -1;
    }

    public void Rewind()
    {
        _next = 0;
        _running = false;
        _remaining = _pulses.Length > 0 ? _pulses[0] : 0;
    }

    /// <summary>Called by the machine before each C64 instruction: <paramref name="motor"/> is the motor line, <paramref name="writeLine"/> the cassette write line.</summary>
    public void Update(long now, bool motor, bool writeLine, Action fall)
    {
        if (Tape == null) return;
        if (Recording && Play) Record(now, motor, writeLine);

        if (motor != _running)
        {
            _running = motor;
            if (motor) _edgeAt = now + _remaining;
            else _remaining = Math.Max(1, _edgeAt - now);
        }
        if (!_running || !Play) return;
        while (_next < _pulses.Length && _edgeAt <= now)
        {
            fall();
            _next++;
            if (_next < _pulses.Length) _edgeAt += _pulses[_next];
            else _remaining = 0;
        }
    }

    void Record(long now, bool motor, bool writeLine)
    {
        if (!motor)
        {
            Flush();
            _lastFall = -1;
        }
        else if (!_writeLevel && writeLine)                       // a rising edge: one pulse since the last
        {
            if (_lastFall >= 0 && now - _lastFall < 1 << 23) _recorded.Add((int)(now - _lastFall));
            _lastFall = now;
        }
        _writeLevel = writeLine;
    }

    /// <summary>Adds what was recorded to the tape (at its end).</summary>
    public void Flush()
    {
        if (_recorded.Count == 0 || Tape == null) return;
        Tape.AppendPulses(_recorded);
        _pulses = Tape.PulseCycles();
        _next = _pulses.Length;                                   // the tape is at its end, ready to rewind
        _recorded.Clear();
    }

    internal void SaveState(BinaryWriter w)
    {
        w.Write(Tape != null);
        w.Write(Play); w.Write(Recording); w.Write(_next); w.Write(_edgeAt); w.Write(_remaining); w.Write(_running);
    }

    internal void LoadState(BinaryReader r)
    {
        bool had = r.ReadBoolean();
        bool play = r.ReadBoolean(), recording = r.ReadBoolean();
        int next = r.ReadInt32();
        long edgeAt = r.ReadInt64(), remaining = r.ReadInt64();
        bool running = r.ReadBoolean();
        if (!had || Tape == null) return;                        // the tape itself is not in the state: only a recorder with a tape takes its place
        Play = play; Recording = recording;
        _next = Math.Min(next, _pulses.Length); _edgeAt = edgeAt; _remaining = remaining; _running = running;
        _recorded.Clear();
        _lastFall = -1;
        _writeLevel = true;
    }
}
