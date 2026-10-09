using C64Basic.Core.Machine;

namespace C64Basic.Core.Rom;

/// <summary>
/// What turns under the 1541's head: the stepper that moves it a half-track at a time, the spindle motor, and the read channel that
/// turns the bit stream of the current track into what the drive's processor sees: a data byte on VIA 2 port A, the SYNC signal on
/// PB7 (low while ten or more 1 bits go by), and a pulse on the byte-ready line every eight bits after that, which sets the V flag
/// (through the SO pin, when CA2 enables it) and is also an edge on CA1. Bits go by at the speed of the zone the track was recorded
/// in: 3.25, 3.5, 3.75 or 4 cycles per bit from the outer zone in, so a 7692-byte track takes 200 ms (300 rpm).
/// The time of the disk is the drive's own processor clock, brought up to date whenever the processor touches a VIA or finishes
/// an instruction.
/// </summary>
public sealed class DiskMechanics
{
    /// <summary>Bit cell length in sixteenths of a drive cycle, by speed zone (0 = tracks 31 and up, 3 = tracks 1-17).</summary>
    static readonly int[] CellTicks = { 64, 60, 56, 52 };

    readonly Drive1541 _drive;

    GcrDisk? _disk;
    int _slot;                       // the half-track under the head: 0 is track 1
    byte[]? _track;
    int _bitPos;                     // the bit about to pass under the head
    int _phase;                      // the stepper's phase, PB0-PB1 of VIA 2
    bool _motor;
    long _next16;                    // when the next bit completes, in sixteenths of a cycle
    int _shift;                      // the last ten bits
    int _bitCount;                   // bits since the last sync or byte
    bool _sync;
    byte _data = 0xFF;
    bool _byteReadyLow;
    long _byteReadyEnds16;

    public DiskMechanics(Drive1541 drive) => _drive = drive;

    public GcrDisk? Disk => _disk;

    /// <summary>The half-track under the head, 0 to 83 (track = value / 2 + 1; odd values are between two tracks).</summary>
    public int HalfTrack => _slot;
    public double Track => _slot / 2.0 + 1;

    public bool MotorOn => _motor;

    /// <summary>The red LED: PB3 of VIA 2.</summary>
    public bool Led { get; private set; }

    /// <summary>The density bits the DOS sets on PB5-PB6 (informational: bits go by at the speed the track was recorded at).</summary>
    public int Density { get; private set; }

    /// <summary>True while the head is over a sync mark (ten or more 1 bits in a row).</summary>
    public bool Sync => _sync;

    /// <summary>The byte the read channel last assembled (what PA reads).</summary>
    public byte Data => _data;

    /// <summary>Times the head went through the whole of a track since power-on (a count of index passes).</summary>
    public long Revolutions { get; private set; }

    /// <summary>Bytes read so far (for tests and for a drive-activity indicator).</summary>
    public long BytesRead { get; private set; }

    long _sensorBlockedUntil = long.MinValue;

    /// <summary>
    /// How long the write-protect sensor is covered while a disk is pushed in or pulled out, in drive cycles. The DOS only
    /// notices that the disk was changed because this sensor flickers, so a swap has to look like one.
    /// </summary>
    const long SwapCycles = 300_000;

    public void Insert(GcrDisk? disk, bool swap = true)
    {
        long now = _drive.Cpu.AccessCycle;
        AdvanceTo(now);
        _disk = disk;
        if (swap) _sensorBlockedUntil = now + SwapCycles;
        SelectTrack(_slot, keepAngle: false);
    }

    int TrackBits => _track == null ? 0 : _track.Length * 8;

    void SelectTrack(int slot, bool keepAngle)
    {
        int oldBits = TrackBits;
        _slot = slot;
        _track = _disk?.Tracks[slot];
        int bits = TrackBits;
        _bitPos = keepAngle && oldBits > 0 && bits > 0 ? (int)((long)_bitPos * bits / oldBits) : 0;
        if (bits > 0 && _bitPos >= bits) _bitPos = 0;
    }

    // ---------- the chip side ----------
    /// <summary>What VIA 2 port B reads: write-protect on PB4 (low when the notch is covered), SYNC on PB7 (low during a sync mark).</summary>
    public byte PinsB()
    {
        AdvanceTo(_drive.Cpu.AccessCycle);
        int pins = 0x6F;
        if (_drive.Cpu.AccessCycle >= _sensorBlockedUntil && (_disk == null || !_disk.WriteProtected)) pins |= 0x10;
        if (!_sync) pins |= 0x80;
        return (byte)pins;
    }

    public byte PinsA()
    {
        AdvanceTo(_drive.Cpu.AccessCycle);
        return _data;
    }

    /// <summary>Port B of VIA 2 changed: the stepper phase, the motor, the LED and the density bits.</summary>
    public void PortBChanged()
    {
        AdvanceTo(_drive.Cpu.AccessCycle);
        byte pb = (byte)(_drive.Via2.Orb & _drive.Via2.Ddrb);
        Led = (pb & 8) != 0;
        Density = pb >> 5 & 3;

        int phase = pb & 3;
        if (phase != _phase)
        {
            _phase = phase;
            // The rotor is pulled to the nearest position that matches the energised phase (position = phase, modulo 4): one step
            // forward moves the head a half-track towards the centre of the disk, one back towards the edge. A stop at each edge
            // holds it, whatever the phase says afterwards.
            int delta = (phase - _slot) & 3;
            int slot = _slot + (delta == 1 ? 1 : delta == 3 ? -1 : 0);
            slot = Math.Clamp(slot, 0, GcrDisk.Slots - 1);
            if (slot != _slot) SelectTrack(slot, keepAngle: true);
        }

        bool motor = (pb & 4) != 0;
        if (motor && !_motor) _next16 = Math.Max(_next16, _drive.Cpu.AccessCycle * 16);
        _motor = motor;
    }

    // ---------- time ----------
    int CellAt(int bitPos) => CellTicks[_disk == null ? 3 : _disk.SpeedAt(_slot, bitPos >> 3)];

    /// <summary>Lets the disk turn up to the given drive cycle, delivering every byte and sync edge on the way.</summary>
    public void AdvanceTo(long cycle)
    {
        long target = cycle * 16;
        if (!_motor || _track == null)
        {
            if (_next16 < target) _next16 = target;
            if (!_motor || _track == null) _sync = false;
            ReleaseByteReady(target);
            return;
        }

        int bits = _track.Length * 8;
        while (_next16 <= target)
        {
            int bit = _track[_bitPos >> 3] >> (7 - (_bitPos & 7)) & 1;
            int cell = CellAt(_bitPos);
            if (++_bitPos >= bits) { _bitPos = 0; Revolutions++; }
            _next16 += cell;
            ReleaseByteReady(_next16);

            _shift = (_shift << 1 | bit) & 0x3FF;
            if (_shift == 0x3FF) { _sync = true; _bitCount = 0; }
            else
            {
                _sync = false;
                if (++_bitCount == 8)
                {
                    _bitCount = 0;
                    _data = (byte)_shift;
                    BytesRead++;
                    ByteReady(_next16);
                }
            }
        }
        ReleaseByteReady(target);
    }

    void ByteReady(long at16)
    {
        _byteReadyLow = true;
        _byteReadyEnds16 = at16 + 2 * 16;
        _drive.Via2.SetCa1(false);                         // the line falls: CA1 sees an edge (and latches port A if asked to)
        if (_drive.Via2.Ca2Output) _drive.Cpu.SetOverflow();   // SOE: byte ready also sets the V flag
    }

    void ReleaseByteReady(long at16)
    {
        if (!_byteReadyLow || at16 < _byteReadyEnds16) return;
        _byteReadyLow = false;
        _drive.Via2.SetCa1(true);
    }
}
