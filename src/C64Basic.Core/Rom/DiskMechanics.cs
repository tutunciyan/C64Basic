using C64Basic.Core.Machine;

namespace C64Basic.Core.Rom;

/// <summary>
/// What turns under the 1541's head: the stepper that moves it a half-track at a time, the spindle motor, and the read channel that
/// turns the bit stream of the current track into what the drive's processor sees: a data byte on VIA 2 port A, the SYNC signal on
/// PB7 (low while ten or more 1 bits go by), and a pulse on the byte-ready line every eight bits after that, which sets the V flag
/// (through the SO pin, when CA2 enables it) and is also an edge on CA1. Bits go by at the speed of the zone the track was recorded
/// in: 3.25, 3.5, 3.75 or 4 cycles per bit from the outer zone in, so a 7692-byte track takes 200 ms (300 rpm). The read channel
/// counts cells at the speed the density bits on PB5-PB6 select, not the speed the track was recorded at: with the right density
/// every bit is read as it was recorded, with another one the stream is sampled at the wrong rate (a bit may be missed or seen twice)
/// as on a real drive, so code that sets the density wrongly, or protection that records at an odd speed, behaves as it would there.
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
    int _frac;                       // how far into that bit the head is, in 1/65536 (not zero only after reading at another density than the track's)
    int _direction = 1;              // which way the head last moved (a jump of two phases carries on that way)
    int _phase;                      // the stepper's phase, PB0-PB1 of VIA 2
    bool _motor;
    long _next16;                    // when the next bit completes, in sixteenths of a cycle
    int _shift;                      // the last ten bits
    int _bitCount;                   // bits since the last sync or byte
    bool _sync;
    byte _data = 0xFF;
    bool _byteReadyLow;
    long _byteReadyEnds16;
    bool _writing, _writeActive, _wroteSomething, _writesPending;
    byte _writeShift;
    int _writeCount;

    public DiskMechanics(Drive1541 drive) => _drive = drive;

    public GcrDisk? Disk => _disk;

    /// <summary>The half-track under the head, 0 to 83 (track = value / 2 + 1; odd values are between two tracks).</summary>
    public int HalfTrack => _slot;
    public double Track => _slot / 2.0 + 1;

    public bool MotorOn => _motor;

    /// <summary>The red LED: PB3 of VIA 2.</summary>
    public bool Led { get; private set; }

    /// <summary>The density bits the DOS sets on PB5-PB6: the speed zone the read and write channels count their cells at.</summary>
    public int Density { get; private set; }

    /// <summary>True while the head is over a sync mark (ten or more 1 bits in a row).</summary>
    public bool Sync => _sync;

    /// <summary>The byte the read channel last assembled (what PA reads).</summary>
    public byte Data => _data;

    /// <summary>Times the head went through the whole of a track since power-on (a count of index passes).</summary>
    public long Revolutions { get; private set; }

    /// <summary>Bytes read so far (for tests and for a drive-activity indicator).</summary>
    public long BytesRead { get; private set; }

    /// <summary>Bytes written to the disk so far.</summary>
    public long BytesWritten { get; private set; }

    /// <summary>True while the write head is on (VIA 2's CB2 is low).</summary>
    public bool Writing => _writing;

    /// <summary>
    /// True once after the drive wrote something and switched back to reading: the disk has changed and can be saved. The call clears it.
    /// </summary>
    public bool TakeWrites()
    {
        bool pending = _writesPending;
        _writesPending = false;
        return pending;
    }

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
        _frac = 0;
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
            // forward moves the head a half-track towards the centre of the disk, one back towards the edge. The opposite coil
            // (a jump of two) pulls both ways alike, so the rotor goes on the way it was already going. A stop at each edge holds
            // it, whatever the phase says afterwards.
            int delta = (phase - _slot) & 3;
            int move = delta == 1 ? 1 : delta == 3 ? -1 : delta == 2 ? 2 * _direction : 0;
            int slot = Math.Clamp(_slot + move, 0, GcrDisk.Slots - 1);
            if (move != 0) _direction = move > 0 ? 1 : -1;
            if (slot != _slot) SelectTrack(slot, keepAngle: true);
        }

        bool motor = (pb & 4) != 0;
        if (motor && !_motor) _next16 = Math.Max(_next16, _drive.Cpu.AccessCycle * 16);
        _motor = motor;
    }

    public void SaveState(BinaryWriter w)
    {
        AdvanceTo(_drive.Cpu.Cycles);
        w.Write(_disk != null);
        _disk?.SaveState(w);
        w.Write(_slot); w.Write(_bitPos); w.Write(_phase); w.Write(_motor);
        w.Write(_next16); w.Write(_shift); w.Write(_bitCount); w.Write(_sync); w.Write(_data);
        w.Write(_byteReadyLow); w.Write(_byteReadyEnds16);
        w.Write(_writing); w.Write(_writeActive); w.Write(_wroteSomething); w.Write(_writesPending);
        w.Write(_writeShift); w.Write(_writeCount);
        w.Write(Led); w.Write(Density); w.Write(Revolutions); w.Write(BytesRead); w.Write(BytesWritten);
        w.Write(_sensorBlockedUntil);
        w.Write(_frac); w.Write(_direction);                  // state version 2
    }

    public void LoadState(BinaryReader r, int version = 2)
    {
        _disk = r.ReadBoolean() ? GcrDisk.LoadState(r) : null;
        _slot = r.ReadInt32(); _bitPos = r.ReadInt32(); _phase = r.ReadInt32(); _motor = r.ReadBoolean();
        _next16 = r.ReadInt64(); _shift = r.ReadInt32(); _bitCount = r.ReadInt32(); _sync = r.ReadBoolean(); _data = r.ReadByte();
        _byteReadyLow = r.ReadBoolean(); _byteReadyEnds16 = r.ReadInt64();
        _writing = r.ReadBoolean(); _writeActive = r.ReadBoolean(); _wroteSomething = r.ReadBoolean(); _writesPending = r.ReadBoolean();
        _writeShift = r.ReadByte(); _writeCount = r.ReadInt32();
        Led = r.ReadBoolean(); Density = r.ReadInt32(); Revolutions = r.ReadInt64(); BytesRead = r.ReadInt64(); BytesWritten = r.ReadInt64();
        _sensorBlockedUntil = r.ReadInt64();
        if (version >= 2) { _frac = r.ReadInt32(); _direction = r.ReadInt32() >= 0 ? 1 : -1; }
        else { _frac = 0; _direction = 1; }
        _track = _disk?.Tracks[Math.Clamp(_slot, 0, GcrDisk.Slots - 1)];
        if (_track != null && _bitPos >= _track.Length * 8) _bitPos = 0;
    }

    /// <summary>The peripheral control register of VIA 2 changed: CB2 low is the write head (read/write select).</summary>
    public void ControlChanged()
    {
        AdvanceTo(_drive.Cpu.AccessCycle);
        bool write = !_drive.Via2.Cb2Output;
        if (write == _writing) return;
        _writing = write;
        _writeActive = false;
        if (write) { _frac = 0; return; }
        _shift = 0;                                        // back to reading: look for a sync mark again
        _bitCount = 0;
        _sync = false;
        if (_wroteSomething) { _writesPending = true; _wroteSomething = false; }
    }

    // ---------- time ----------
    int ZoneAt(int bitPos) => _disk == null ? 3 : _disk.SpeedAt(_slot, bitPos >> 3);

    /// <summary>Lets the disk turn up to the given drive cycle, delivering every byte and sync edge on the way.</summary>
    public void AdvanceTo(long cycle)
    {
        long target = cycle * 16;
        if (_track == null && _writing && _motor && _disk is { WriteProtected: false }) _track = _disk.EnsureTrack(_slot);
        if (!_motor || _track == null)
        {
            if (_next16 < target) _next16 = target;
            _sync = false;
            ReleaseByteReady(target);
            return;
        }

        int bits = _track.Length * 8;
        while (_next16 <= target)
        {
            int position = _bitPos;
            int cell = CellTicks[Density];
            int bit;
            if (!_writing && (_frac != 0 || ZoneAt(position) != Density)) bit = Resample(position, cell, bits);
            else
            {
                bit = _track[position >> 3] >> (7 - (position & 7)) & 1;
                if (++_bitPos >= bits) { _bitPos = 0; Revolutions++; }
            }
            _next16 += cell;
            ReleaseByteReady(_next16);

            if (_writing && _writeActive)
            {
                // the head writes the bit under it: the byte the processor put in port A, most significant bit first
                if (_disk is { WriteProtected: false })
                {
                    _disk.SetSpeed(_slot, position >> 3, Density);           // the bit goes onto the disk at the density the drive is set to
                    int mask = 0x80 >> (position & 7);
                    if ((_writeShift & 0x80) != 0) _track[position >> 3] |= (byte)mask; else _track[position >> 3] &= (byte)~mask;
                    _disk.Modified = true;
                    _wroteSomething = true;
                }
                _writeShift <<= 1;
                if (++_writeCount == 8)
                {
                    _writeCount = 0;
                    _writeShift = _drive.Via2.PortAOutput;
                    BytesWritten++;
                    ByteReady(_next16);
                }
                continue;
            }

            _shift = (_shift << 1 | bit) & 0x3FF;
            if (_shift == 0x3FF) { _sync = true; _bitCount = 0; }
            else
            {
                _sync = false;
                if (++_bitCount == 8)
                {
                    _bitCount = 0;
                    if (_writing)
                    {
                        // the write head switches on at the next byte boundary and loads the byte waiting in port A
                        _writeActive = true;
                        _writeCount = 0;
                        _writeShift = _drive.Via2.PortAOutput;
                        ByteReady(_next16);
                        continue;
                    }
                    _data = (byte)_shift;
                    BytesRead++;
                    ByteReady(_next16);
                }
            }
        }
        ReleaseByteReady(target);
    }

    /// <summary>
    /// One cell of the read channel at a density that is not the one the track was recorded at: the head moves on by the ratio of the
    /// two cell lengths, and the bit it reports is 1 if the middle of any recorded bit (where its flux transition is) passed under it
    /// during the cell. At equal speeds that is exactly the next recorded bit.
    /// </summary>
    int Resample(int position, int cell, int bits)
    {
        long ratio = ((long)cell << 16) / CellTicks[ZoneAt(position)];
        long from = (long)position << 16 | (uint)_frac, to = from + ratio;
        int bit = 0;
        for (long i = (from - 0x8000 + 0xFFFF) >> 16; i <= (to - 0x8001) >> 16; i++)       // the bits whose middle is in [from, to)
        {
            int n = (int)(i % bits);
            bit |= _track![n >> 3] >> (7 - (n & 7)) & 1;
        }
        _bitPos = (int)(to >> 16);
        _frac = (int)(to & 0xFFFF);
        if (_bitPos >= bits) { _bitPos %= bits; Revolutions++; }
        return bit;
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
