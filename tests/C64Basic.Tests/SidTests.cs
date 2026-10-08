using C64Basic.Core.Machine;

namespace C64Basic.Tests;

public class SidTests
{
    const int Rate = 44100;

    static short[] Render(Bus bus, int count = Rate)
    {
        var buffer = new short[count];
        bus.Sound.Render(buffer, Rate);
        return buffer;
    }

    /// <summary>Voice 1 playing a tone at the given frequency (Hz) with full sustain and volume.</summary>
    static Bus Tone(int waveform, double hz)
    {
        var bus = new Bus();
        int freq = (int)Math.Round(hz * 16777216 / Sid.ClockHz);
        bus.Write(0xD400, (byte)(freq & 0xFF));
        bus.Write(0xD401, (byte)(freq >> 8));
        bus.Write(0xD402, 0x00);
        bus.Write(0xD403, 0x08);               // 50 % pulse
        bus.Write(0xD405, 0x00);               // fastest attack and decay
        bus.Write(0xD406, 0xF0);               // sustain 15
        bus.Write(0xD418, 0x0F);
        bus.Write(0xD404, (byte)(waveform | 1));
        return bus;
    }

    static int ZeroCrossings(short[] s)
    {
        int n = 0;
        for (int i = 1; i < s.Length; i++) if (s[i - 1] < 0 != s[i] < 0) n++;
        return n;
    }

    static double Rms(short[] s, int skip = 0) =>
        Math.Sqrt(s.Skip(skip).Select(x => (double)x * x).Average());

    [Fact]
    public void SilentUntilGated()
    {
        var bus = new Bus();
        bus.Write(0xD418, 0x0F);
        bus.Write(0xD401, 0x20);
        bus.Write(0xD404, 0x20);               // sawtooth but no gate
        Assert.All(Render(bus, 1000), s => Assert.Equal(0, s));
    }

    [Fact]
    public void TriangleHasTheRightPitch()
    {
        var samples = Render(Tone(0x10, 440));
        Assert.InRange(ZeroCrossings(samples), 870, 890); // two per cycle
    }

    [Fact]
    public void SawtoothHasTheRightPitch()
    {
        var samples = Render(Tone(0x20, 220));
        Assert.InRange(ZeroCrossings(samples), 435, 445);
    }

    [Fact]
    public void PulseWidthChangesTheDutyCycle()
    {
        var bus = Tone(0x40, 100);
        var half = Render(bus);
        Assert.InRange(half.Count(s => s > 0) / (double)half.Length, 0.45, 0.55);

        bus.Write(0xD403, 0x0C);               // 75 % low
        var quarter = Render(bus);
        Assert.InRange(quarter.Count(s => s > 0) / (double)quarter.Length, 0.20, 0.30);
    }

    [Fact]
    public void NoiseIsNotPeriodic()
    {
        var samples = Render(Tone(0x80, 4000), 4000);
        Assert.True(samples.Distinct().Count() > 10);
    }

    [Fact]
    public void MasterVolumeScalesOutput()
    {
        var bus = Tone(0x20, 220);
        double loud = Rms(Render(bus, 4000), 500);
        bus.Write(0xD418, 0x07);
        double quiet = Rms(Render(bus, 4000), 500);
        Assert.InRange(quiet / loud, 0.4, 0.55);
        bus.Write(0xD418, 0x00);
        Assert.All(Render(bus, 100), s => Assert.Equal(0, s));
    }

    [Fact]
    public void EnvelopeReachesSustainLevelAndReleases()
    {
        var bus = Tone(0x20, 220);
        bus.Write(0xD406, 0x80);               // sustain 8 = level 136
        bus.Write(0xD40D, 0x80);
        // voice 3 mirrors the settings so its envelope can be read back at 54284
        bus.Write(0xD40E, 0x00); bus.Write(0xD40F, 0x04);
        bus.Write(0xD413, 0x00); bus.Write(0xD414, 0x80);
        bus.Write(0xD412, 0x21);
        Render(bus, Rate);
        Assert.Equal(136, bus.Read(0xD41C));

        bus.Write(0xD414, 0x80 | 0);           // release rate 0
        bus.Write(0xD412, 0x20);               // gate off
        Render(bus, Rate / 10);
        Assert.Equal(0, bus.Read(0xD41C));
    }

    [Fact]
    public void AttackTakesLongerThanFastAttack()
    {
        var fast = Tone(0x20, 220);
        var slow = Tone(0x20, 220);
        slow.Write(0xD404, 0x20);
        slow.Write(0xD405, 0x80);              // attack rate 8: ~100 ms
        slow.Write(0xD404, 0x21);
        double fastStart = Rms(Render(fast, 500));
        double slowStart = Rms(Render(slow, 500));
        Assert.True(slowStart < fastStart * 0.6);
    }

    [Fact]
    public void LowPassFilterRemovesHighTone()
    {
        var plain = Tone(0x10, 3000);
        double open = Rms(Render(plain, 8000), 1000);

        var filtered = Tone(0x10, 3000);
        filtered.Write(0xD415, 0x00);
        filtered.Write(0xD416, 0x10);          // low cutoff
        filtered.Write(0xD417, 0x01);          // route voice 1 through the filter
        filtered.Write(0xD418, 0x1F);          // low pass, volume 15
        double closed = Rms(Render(filtered, 8000), 1000);
        Assert.True(closed < open * 0.3, $"{closed} vs {open}");
    }

    [Fact]
    public void TestBitHoldsTheOscillator()
    {
        var bus = Tone(0x20, 220);
        bus.Write(0xD404, 0x29);
        var samples = Render(bus, 2000);
        Assert.Single(samples.Skip(500).Distinct());
    }

    [Fact]
    public void RingModulationChangesTheSound()
    {
        var plain = Tone(0x10, 440);
        plain.Write(0xD40E, 0x00); plain.Write(0xD40F, 0x30);    // voice 3 runs fast
        plain.Write(0xD412, 0x10);
        var before = Render(plain, 2000);

        var ring = Tone(0x10, 440);
        ring.Write(0xD40E, 0x00); ring.Write(0xD40F, 0x30);
        ring.Write(0xD412, 0x10);
        ring.Write(0xD404, 0x15);                                // triangle + ring + gate
        var after = Render(ring, 2000);
        Assert.NotEqual(before, after);
    }

    [Fact]
    public void Voice3OscillatorIsReadable()
    {
        var bus = new Bus();
        bus.Write(0xD40F, 0x20);
        bus.Write(0xD412, 0x20);
        var seen = new HashSet<int>();
        for (int i = 0; i < 20; i++)
        {
            Render(bus, 37);
            seen.Add(bus.Read(0xD41B));
        }
        Assert.True(seen.Count > 5);
    }

    [Fact]
    public void RegistersMirrorEvery32Bytes()
    {
        var bus = new Bus();
        bus.Write(0xD418 + 32, 0x0F);
        bus.Write(0xD401 + 64, 0x10);
        bus.Write(0xD406 + 96, 0xF0);
        bus.Write(0xD404 + 128, 0x21);
        Assert.True(Render(bus, 2000).Max() > 0);
    }

    [Fact]
    public void BasicCanPlayANote()
    {
        var (_, interp, _) = Basic.Session(
            "POKE 54296,15:POKE 54277,0:POKE 54278,240",
            "POKE 54273,29:POKE 54272,69:POKE 54276,17");
        var buffer = new short[Rate];
        interp.Bus.Sound.Render(buffer, Rate);
        Assert.InRange(ZeroCrossings(buffer), 870, 890);
    }
}

public class SidAccuracyTests
{
    static Bus Chip(Sid.SidModel model) { var bus = new Bus(); bus.Sound.Model = model; return bus; }

    [Fact]
    public void TheDefaultChipIsA6581() => Assert.Equal(Sid.SidModel.Mos6581, new Bus().Sound.Model);

    [Fact]
    public void FilterCurvesDifferBetweenTheModels()
    {
        var old = Chip(Sid.SidModel.Mos6581).Sound;
        var cleaner = Chip(Sid.SidModel.Mos8580).Sound;
        Assert.True(old.CutoffHz(1024) < cleaner.CutoffHz(1024) * 0.6);     // the 6581 stays low until late in the range
        Assert.InRange(cleaner.CutoffHz(1024), 5500, 6500);                  // the 8580 is linear
        Assert.Equal(old.CutoffHz(2047), cleaner.CutoffHz(2047), 1);        // both reach the top
        Assert.True(old.CutoffHz(100) < old.CutoffHz(900));                  // and rise monotonically
    }

    static short[] Play(Bus bus, int waveform, int count = 4000)
    {
        bus.Write(0xD400, 0x00); bus.Write(0xD401, 0x10);
        bus.Write(0xD403, 0x08);
        bus.Write(0xD405, 0x00); bus.Write(0xD406, 0xF0);
        bus.Write(0xD418, 0x0F);
        bus.Write(0xD404, (byte)(waveform | 1));
        var buffer = new short[count];
        bus.Sound.Render(buffer, 44100);
        return buffer;
    }

    static double Level(short[] s) => s.Skip(500).Sum(x => (double)x);

    static double Energy(short[] s) => s.Skip(500).Sum(x => (double)x * x);

    [Fact]
    public void NoiseCombinedWithAnotherWaveformGoesSilent()
    {
        var bus = Chip(Sid.SidModel.Mos6581);
        Assert.True(Energy(Play(bus, 0x80)) > 0);
        var locked = Chip(Sid.SidModel.Mos6581);
        Assert.Equal(0, Energy(Play(locked, 0x80 | 0x20)), 1);
    }

    [Fact]
    public void CombinedWaveformsAreWeakerThanTheirParts()
    {
        double saw = Level(Play(Chip(Sid.SidModel.Mos6581), 0x20));
        double both = Level(Play(Chip(Sid.SidModel.Mos6581), 0x30));       // triangle + saw
        Assert.True(both < saw);
        Assert.True(Energy(Play(Chip(Sid.SidModel.Mos6581), 0x30)) > 0);
    }

    [Fact]
    public void The8580KeepsMoreOfACombinedWaveform()
    {
        double old = Level(Play(Chip(Sid.SidModel.Mos6581), 0x30));
        double newer = Level(Play(Chip(Sid.SidModel.Mos8580), 0x30));
        Assert.True(newer > old);
    }
}
