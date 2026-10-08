namespace C64Basic.Core.Disk;

/// <summary>
/// Relative files: fixed-length records packed into the data blocks, plus side sectors that list every data block. A side
/// sector holds the track/sector of up to 120 data blocks and, in its header, of all (at most six) side sectors of the file.
/// </summary>
public sealed partial class D64Image
{
    const int SideSectorBlocks = 120, MaxSideSectors = 6, RelType = 4;

    public IRelFile? OpenRel(string name, int recordLength)
    {
        var slot = Find(name, null);
        if (slot != null)
        {
            if (TypeOf(EntryAt(slot)[2]) != FileType.Rel)
            {
                if (recordLength > 0) throw new DriveException(64);
                return null;
            }
            return new RelHandle(this, slot);
        }
        return recordLength > 0 ? CreateRel(name, recordLength) : null;
    }

    RelHandle CreateRel(string name, int recordLength)
    {
        DosText.ValidateName(name);
        if (recordLength is < 1 or > 254) throw new DriveException(30);
        if (BlocksFree < 2) throw new DriveException(72);

        var slot = FreeSlot();
        var (dt, ds) = Allocate(0, 0, FileInterleave);
        var data = Sector(dt, ds);
        data.Clear();
        data[1] = (byte)(1 + recordLength);                 // the first record, empty
        data[2] = 0xFF;

        var entry = EntryAt(slot);
        entry[2..].Clear();
        entry[2] = 0x80 | RelType;
        entry[3] = (byte)dt; entry[4] = (byte)ds;
        Pad(entry.Slice(5, 16), name);
        entry[23] = (byte)recordLength;

        var handle = new RelHandle(this, slot);
        handle.RebuildSideSectors(new List<(int, int)> { (dt, ds) });
        Changed();
        return handle;
    }

    void FreeSideChain(Span<byte> entry)
    {
        int track = entry[21], sector = entry[22], guard = 0;
        while (track != 0 && guard++ < MaxSideSectors + 1)
        {
            int next = Sector(track, sector)[0], nextSector = Sector(track, sector)[1];
            SetFree(track, sector, true);
            track = next; sector = nextSector;
        }
    }

    void MarkSideChain(Span<byte> entry)
    {
        int track = entry[21], sector = entry[22], guard = 0;
        while (track != 0 && guard++ < MaxSideSectors + 1)
        {
            SetFree(track, sector, false);
            int next = Sector(track, sector)[0], nextSector = Sector(track, sector)[1];
            track = next; sector = nextSector;
        }
    }

    sealed class RelHandle : IRelFile
    {
        readonly D64Image _d;
        readonly Slot _slot;

        public RelHandle(D64Image d, Slot slot)
        {
            _d = d;
            _slot = slot;
        }

        public int RecordLength => _d.EntryAt(_slot)[23];

        List<(int Track, int Sector)> Chain()
        {
            var chain = new List<(int, int)>();
            var e = _d.EntryAt(_slot);
            int track = e[3], sector = e[4];
            while (track != 0 && chain.Count <= MaxSideSectors * SideSectorBlocks)
            {
                chain.Add((track, sector));
                var s = _d.Sector(track, sector);
                int next = s[0];
                sector = s[1];
                track = next;
            }
            return chain;
        }

        int TotalBytes(List<(int Track, int Sector)> chain)
        {
            var (t, s) = chain[^1];
            return (chain.Count - 1) * 254 + (_d.Sector(t, s)[1] - 1);
        }

        public int RecordCount => TotalBytes(Chain()) / RecordLength;

        public byte[] Read(int record)
        {
            var chain = Chain();
            int length = RecordLength, offset = record * length;
            if (record < 0 || offset + length > TotalBytes(chain)) throw new DriveException(50);

            var data = new byte[length];
            for (int i = 0; i < length; i++)
            {
                int at = offset + i;
                var (t, s) = chain[at / 254];
                data[i] = _d.Sector(t, s)[2 + at % 254];
            }
            return data;
        }

        public void Write(int record, byte[] data)
        {
            var chain = Chain();
            int length = RecordLength;
            if (record < 0) throw new DriveException(50);
            int total = TotalBytes(chain), needed = (record + 1) * length;

            if (needed > total)
            {
                int blocksNeeded = (needed + 253) / 254;
                if (blocksNeeded > MaxSideSectors * SideSectorBlocks) throw new DriveException(52);
                if (blocksNeeded - chain.Count > _d.BlocksFree - 1) throw new DriveException(72); // one more for a side sector

                // new data blocks, linked after the last one
                while (chain.Count < blocksNeeded)
                {
                    var (lt, ls) = chain[^1];
                    var (nt, ns) = _d.Allocate(lt, ls, FileInterleave);
                    _d.Sector(nt, ns).Clear();
                    _d.Sector(lt, ls)[0] = (byte)nt;
                    _d.Sector(lt, ls)[1] = (byte)ns;
                    chain.Add((nt, ns));
                }

                // every record the file did not have yet starts empty: $FF, then zeros
                int firstNew = total / length;
                for (int r = firstNew; r <= record; r++)
                    for (int i = 0; i < length; i++)
                        SetByte(chain, r * length + i, i == 0 ? (byte)0xFF : (byte)0);

                // the last block ends where the last record ends
                var (et, es) = chain[^1];
                var last = _d.Sector(et, es);
                last[0] = 0;
                last[1] = (byte)(needed - (chain.Count - 1) * 254 + 1);
                RebuildSideSectors(chain);
            }

            for (int i = 0; i < length; i++) SetByte(chain, record * length + i, i < data.Length ? data[i] : (byte)0);
            _d.Changed();
        }

        void SetByte(List<(int Track, int Sector)> chain, int offset, byte value)
        {
            var (t, s) = chain[offset / 254];
            _d.Sector(t, s)[2 + offset % 254] = value;
        }

        /// <summary>Frees the old side sectors and writes new ones that list every block of <paramref name="chain"/>.</summary>
        public void RebuildSideSectors(List<(int Track, int Sector)> chain)
        {
            var entry = _d.EntryAt(_slot);
            _d.FreeSideChain(entry);

            int count = (chain.Count + SideSectorBlocks - 1) / SideSectorBlocks;
            var sides = new List<(int Track, int Sector)>();
            var (pt, ps) = chain[^1];
            for (int i = 0; i < count; i++)
            {
                var next = _d.Allocate(pt, ps, 1);
                sides.Add(next);
                (pt, ps) = next;
            }

            for (int i = 0; i < count; i++)
            {
                var block = _d.Sector(sides[i].Track, sides[i].Sector);
                block.Clear();
                int inThis = Math.Min(SideSectorBlocks, chain.Count - i * SideSectorBlocks);
                if (i < count - 1) { block[0] = (byte)sides[i + 1].Track; block[1] = (byte)sides[i + 1].Sector; }
                else { block[0] = 0; block[1] = (byte)(15 + 2 * inThis); }
                block[2] = (byte)i;
                block[3] = (byte)RecordLength;
                for (int j = 0; j < count; j++) { block[4 + 2 * j] = (byte)sides[j].Track; block[5 + 2 * j] = (byte)sides[j].Sector; }
                for (int j = 0; j < inThis; j++)
                {
                    var (t, s) = chain[i * SideSectorBlocks + j];
                    block[16 + 2 * j] = (byte)t; block[17 + 2 * j] = (byte)s;
                }
            }

            entry[21] = (byte)sides[0].Track; entry[22] = (byte)sides[0].Sector;
            int blocks = chain.Count + count;
            entry[30] = (byte)blocks; entry[31] = (byte)(blocks >> 8);
        }
    }
}
