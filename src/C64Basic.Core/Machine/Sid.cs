namespace C64Basic.Core.Machine;

/// <summary>
/// The 6581/8580 sound chip, 54272-54300, mirrored every 32 bytes through 54272-55295. Three voices with
/// triangle, sawtooth, pulse and noise waveforms, ring modulation, hard sync, ADSR envelopes, a
/// state-variable filter and the master volume. The host pulls mono samples with <see cref="Render"/>.
/// Combined waveforms are modelled per chip: the 6581 loses the bits a neighbouring low bit pulls down, the 8580 passes
/// more of the signal, and noise combined with another waveform locks up as on the real chip. The filter cutoff follows each
/// model's curve (see <see cref="Model"/>).
/// </summary>
public sealed class Sid : IMemoryMapped
{
    public const int Start = 0xD400, Length = 0x400, RegisterCount = 29;

    /// <summary>PAL system clock.</summary>
    public const double ClockHz = 985248;

    /// <summary>The chip revision: it changes combined waveforms, the filter curve and resonance.</summary>
    public enum SidModel { Mos6581, Mos8580 }

    /// <summary>Which chip is fitted. The 6581 (default) is the original chip with the dark, strongly non-linear filter.</summary>
    public SidModel Model { get; set; } = SidModel.Mos6581;

    /// <summary>Filter corner frequency for an 11-bit cutoff value.</summary>
    public double CutoffHz(int cutoff)
    {
        double x = cutoff / 2047.0;
        return Model == SidModel.Mos6581
            ? 30 + 11970 * Math.Pow(x, 2)          // bunched up at the low end, a steep rise at the top
            : 30 + 11970 * x;                         // the 8580 is nearly linear
    }

    const int Triangle = 0x10, Saw = 0x20, Pulse = 0x40, Noise = 0x80, Test = 0x08, RingMod = 0x04, Sync = 0x02, Gate = 0x01;

    // clock cycles per envelope step: attack, and the same table (times 3 per decay step) for decay/release
    static readonly int[] RatePeriod =
        { 9, 32, 63, 95, 149, 220, 267, 313, 392, 977, 1954, 3126, 3907, 11720, 19532, 31251 };

    enum Stage { Attack, Decay, Sustain, Release }

    sealed class Voice
    {
        public int Frequency, PulseWidth, Control, AttackDecay, SustainRelease;
        public double Phase;               // 24-bit accumulator
        public int Lfsr = 0x7FFFF8;        // 23-bit noise shift register
        public bool PhaseMsb, MsbRose;
        public int Level;                  // envelope, 0-255
        public Stage Stage = Stage.Release;
        public double EnvCycles;
        public int Output;                 // 12-bit waveform
        public bool Gate => (Control & Sid.Gate) != 0;
    }

    readonly object _gate = new();
    readonly Voice[] _voice = { new(), new(), new() };
    int _filterCutoff, _filterControl, _modeVolume;
    double _low, _band;

    /// <summary>Supplies the paddle position (0-255) on the POTX (0) or POTY (1) line of the selected game port.</summary>
    public Func<int, int>? Pot { get; set; }

    public byte Read(int address)
    {
        lock (_gate)
        {
            return ((address - Start) & 31) switch
            {
                0x1B => (byte)(_voice[2].Output >> 4),
                0x1C => (byte)_voice[2].Level,
                0x19 => (byte)(Pot?.Invoke(0) ?? 0),
                0x1A => (byte)(Pot?.Invoke(1) ?? 0),
                _ => 0, // the other registers are write-only
            };
        }
    }

    public void Write(int address, byte value)
    {
        int r = (address - Start) & 31;
        if (r >= RegisterCount) return;
        lock (_gate)
        {
            if (r < 21)
            {
                var v = _voice[r / 7];
                switch (r % 7)
                {
                    case 0: v.Frequency = v.Frequency & 0xFF00 | value; break;
                    case 1: v.Frequency = v.Frequency & 0x00FF | value << 8; break;
                    case 2: v.PulseWidth = v.PulseWidth & 0xF00 | value; break;
                    case 3: v.PulseWidth = v.PulseWidth & 0x0FF | (value & 15) << 8; break;
                    case 4:
                        bool wasGate = v.Gate;
                        v.Control = value;
                        if (v.Gate && !wasGate) v.Stage = Stage.Attack;
                        else if (!v.Gate && wasGate) v.Stage = Stage.Release;
                        if ((value & Test) != 0) { v.Phase = 0; v.Lfsr = 0x7FFFF8; }
                        break;
                    case 5: v.AttackDecay = value; break;
                    case 6: v.SustainRelease = value; break;
                }
            }
            else switch (r)
            {
                case 21: _filterCutoff = _filterCutoff & 0x7F8 | value & 7; break;
                case 22: _filterCutoff = _filterCutoff & 0x007 | value << 3; break;
                case 23: _filterControl = value; break;
                case 24: _modeVolume = value; break;
            }
        }
    }

    /// <summary>The registers; oscillator phase and envelopes start over, which is inaudible after a pause.</summary>
    internal void SaveState(BinaryWriter w)
    {
        lock (_gate)
        {
            foreach (var v in _voice)
            {
                w.Write(v.Frequency); w.Write(v.PulseWidth); w.Write(v.Control); w.Write(v.AttackDecay); w.Write(v.SustainRelease);
            }
            w.Write(_filterCutoff); w.Write(_filterControl); w.Write(_modeVolume);
        }
    }

    internal void LoadState(BinaryReader r)
    {
        lock (_gate)
        {
            foreach (var v in _voice)
            {
                v.Frequency = r.ReadInt32(); v.PulseWidth = r.ReadInt32(); v.Control = r.ReadInt32();
                v.AttackDecay = r.ReadInt32(); v.SustainRelease = r.ReadInt32();
                v.Phase = 0;
                v.EnvCycles = 0;
                // a held note carries on at its sustain level instead of attacking again
                v.Stage = v.Gate ? Stage.Sustain : Stage.Release;
                v.Level = v.Gate ? (v.SustainRelease >> 4) * 17 : 0;
            }
            _filterCutoff = r.ReadInt32(); _filterControl = r.ReadInt32(); _modeVolume = r.ReadInt32();
            _low = _band = 0;
        }
    }

    /// <summary>Produces mono 16-bit samples for the current register state, advancing the chip in time.</summary>
    public void Render(Span<short> samples, int sampleRate)
    {
        double cycles = ClockHz / sampleRate;
        lock (_gate)
        {
            for (int i = 0; i < samples.Length; i++) samples[i] = NextSample(cycles);
        }
    }

    short NextSample(double cycles)
    {
        for (int n = 0; n < 3; n++) Step(n, cycles);

        double filtered = 0, direct = 0;
        for (int n = 0; n < 3; n++)
        {
            var v = _voice[n];
            if (n == 2 && (_modeVolume & 0x80) != 0 && (_filterControl >> 2 & 1) == 0) continue; // voice 3 off
            double s = (v.Output - 2048) * v.Level / 255.0;
            if ((_filterControl >> n & 1) != 0) filtered += s; else direct += s;
        }

        // state-variable filter; cutoff 0-2047 maps roughly 30 Hz-12 kHz
        double fc = 2 * Math.Sin(Math.PI * CutoffHz(_filterCutoff) / 44100);
        double resonance = (_filterControl >> 4 & 15) / 15.0;
        double q = 1.0 - (Model == SidModel.Mos6581 ? 0.9 : 0.95) * resonance;
        double high = filtered - _low - q * _band;
        _band += fc * high;
        _low += fc * _band;
        double mixed = direct;
        if ((_modeVolume & 0x10) != 0) mixed += _low;
        if ((_modeVolume & 0x20) != 0) mixed += _band;
        if ((_modeVolume & 0x40) != 0) mixed += high;

        double volume = (_modeVolume & 15) / 15.0;
        return (short)Math.Clamp(mixed * volume * 4, short.MinValue, short.MaxValue);
    }

    void Step(int n, double cycles)
    {
        var v = _voice[n];
        var prev = _voice[(n + 2) % 3];

        if ((v.Control & Sync) != 0 && prev.MsbRose) v.Phase = 0; // hard sync to the previous voice
        if ((v.Control & Test) == 0)
        {
            double before = v.Phase;
            v.Phase += v.Frequency * cycles;
            if (v.Phase >= 0x1000000) v.Phase -= 0x1000000 * Math.Floor(v.Phase / 0x1000000);
            // noise register shifts when bit 19 of the accumulator rises
            if (((int)v.Phase >> 19 & 1) != 0 && ((int)before >> 19 & 1) == 0)
            {
                int bit = (v.Lfsr >> 22 ^ v.Lfsr >> 17) & 1;
                v.Lfsr = (v.Lfsr << 1 & 0x7FFFFF) | bit;
            }
        }
        bool msb = ((int)v.Phase & 0x800000) != 0;
        v.MsbRose = msb && !v.PhaseMsb;
        v.PhaseMsb = msb;

        v.Output = Waveform(v, prev);
        StepEnvelope(v, cycles);
    }

    int Waveform(Voice v, Voice prev)
    {
        int acc = (int)v.Phase;
        int output = 0xFFF;
        int selected = v.Control & 0xF0;
        if (selected == 0) return 0x800;
        int combined = selected & ~Noise;
        bool several = (combined & (combined - 1)) != 0;     // more than one of triangle/saw/pulse
        if ((selected & Triangle) != 0)
        {
            int t = acc >> 11 & 0xFFF;
            if (((v.Control & RingMod) != 0 ? acc ^ (int)prev.Phase : acc) >> 23 != 0) t ^= 0xFFF;
            output &= t;
        }
        if ((selected & Saw) != 0) output &= acc >> 12;
        if ((selected & Pulse) != 0) output &= (acc >> 12) >= v.PulseWidth ? 0xFFF : 0;
        if (several) output = Bleed(output, Model == SidModel.Mos6581 ? 0.5 : 0.2);
        if ((selected & Noise) != 0)
        {
            if (selected != Noise)
            {
                v.Lfsr = 0;                                    // noise with anything else shifts zeros in until it is reset
                return 0x800;
            }
            int l = v.Lfsr;
            int n = (l >> 20 & 1) << 7 | (l >> 18 & 1) << 6 | (l >> 14 & 1) << 5 | (l >> 11 & 1) << 4
                  | (l >> 9 & 1) << 3 | (l >> 5 & 1) << 2 | (l >> 2 & 1) << 1 | (l & 1);
            output &= n << 4;
        }
        return output;
    }

    /// <summary>
    /// In a combined waveform the bits pull on each other: a one next to a zero is weakened, so isolated high bits fade and the
    /// output keeps its shape without being a hard AND. <paramref name="pull"/> is how strongly a zero neighbour drags a bit down.
    /// </summary>
    static int Bleed(int bits, double pull)
    {
        int result = 0;
        for (int i = 0; i < 12; i++)
        {
            double level = bits >> i & 1;
            double below = i > 0 ? bits >> (i - 1) & 1 : level, above = i < 11 ? bits >> (i + 1) & 1 : level;
            level -= pull * ((1 - below) + (1 - above)) / 2 * level;
            if (level > 0.5) result |= 1 << i;
        }
        return result;
    }

    void StepEnvelope(Voice v, double cycles)
    {
        int attack = v.AttackDecay >> 4, decay = v.AttackDecay & 15;
        int sustain = (v.SustainRelease >> 4) * 17, release = v.SustainRelease & 15;
        v.EnvCycles += cycles;

        while (true)
        {
            int period = v.Stage switch
            {
                Stage.Attack => RatePeriod[attack],
                Stage.Decay => RatePeriod[decay] * 3 * ExponentialFactor(v.Level),
                Stage.Release => RatePeriod[release] * 3 * ExponentialFactor(v.Level),
                _ => 0,
            };
            if (v.Stage == Stage.Sustain)
            {
                v.EnvCycles = 0;
                if (v.Level > sustain) v.Stage = Stage.Decay; // sustain lowered while held
                return;
            }
            if (v.EnvCycles < period) return;
            v.EnvCycles -= period;

            switch (v.Stage)
            {
                case Stage.Attack:
                    if (++v.Level >= 255) { v.Level = 255; v.Stage = Stage.Decay; }
                    break;
                case Stage.Decay:
                    if (v.Level <= sustain) { v.Stage = Stage.Sustain; break; }
                    v.Level--;
                    if (v.Level <= sustain) v.Stage = Stage.Sustain;
                    break;
                case Stage.Release:
                    if (v.Level > 0) v.Level--;
                    else { v.EnvCycles = 0; return; }
                    break;
            }
        }
    }

    /// <summary>The decay/release curve is exponential: the step rate slows at fixed envelope levels.</summary>
    static int ExponentialFactor(int level) => level switch
    {
        > 93 => 1,
        > 54 => 2,
        > 26 => 4,
        > 14 => 8,
        > 6 => 16,
        _ => 30,
    };
}
