using C64Basic.Core.Disk;

namespace C64Basic.Core.Runtime;

/// <summary>Relative files: OPEN n,8,n,"NAME,L,"+CHR$(length), positioning with the P command, PRINT# and INPUT# by record.</summary>
public sealed partial class Interpreter
{
    /// <summary>Opens the file as a relative file if it is one (or is being created as one); false means it is an ordinary file.</summary>
    bool TryOpenRel(BasicFile f, string spec, int sa)
    {
        if (sa is < 2 or > 14) return false;
        int l = spec.IndexOf(",L,", StringComparison.OrdinalIgnoreCase);
        if (l < 0 && spec.Contains(',')) return false; // an explicit ",S,W" mode means an ordinary file
        string path = (l >= 0 ? spec[..l] : spec).Trim();
        if (path.Length == 0) return false;
        int recordLength = l >= 0 && l + 3 < spec.Length ? DosText.ToByte(spec[l + 3]) : 0;

        try
        {
            var rel = DriveFor(f.Device).OpenRel(path, recordLength);
            if (rel == null) return false;
            f.Path = path;
            f.Rel = rel;
            f.Writing = true; // read and written like the command channel
            SetStatus(f.Device, DriveStatus.Ok);
        }
        catch (DriveException e)
        {
            // the drive refuses (wrong type, disk full, ...): the status says why and the channel yields nothing
            SetStatus(f.Device, DriveStatus.Of(e.Code, e.Track, e.Sector));
            f.Path = path;
        }
        return true;
    }

    /// <summary>The next byte of the current record; at the end of a record a CR stands in and the next record becomes current.</summary>
    int RelReadChar(BasicFile f)
    {
        if (f.Rel == null) { _st = 64; return -1; }
        if (f.RelPendingCr) { f.RelPendingCr = false; return '\r'; }

        if (f.RelData == null)
        {
            try { f.RelData = f.Rel.Read(f.RelRecord); }
            catch (DriveException e)
            {
                SetStatus(f.Device, DriveStatus.Of(e.Code, e.Track, e.Sector)); // 50: RECORD NOT PRESENT
                _st = 64;
                return -1;
            }
        }

        // a record that was never written starts with $FF
        if (f.RelData[0] == 0xFF && f.RelPos == 0) { EndRelRecord(f); return '\r'; }

        byte b = f.RelData[f.RelPos++];
        if (b == 13) { EndRelRecord(f); return '\r'; }
        if (f.RelPos >= f.RelData.Length) { EndRelRecord(f); f.RelPendingCr = true; }
        return Petscii.ToChar(b);
    }

    void EndRelRecord(BasicFile f)
    {
        f.RelRecord++;
        f.RelPos = 0;
        f.RelData = null;
        _st = f.RelRecord >= f.Rel!.RecordCount ? 64 : 0;
    }

    /// <summary>PRINT# to a relative file: bytes collect until a CR ends the record, which is then stored.</summary>
    void RelWrite(BasicFile f, string text)
    {
        if (f.Rel == null) return;
        foreach (char c in text)
        {
            byte b = DosText.ToByte(c);
            if (f.RelPos + f.RelOut.Count >= f.Rel.RecordLength)
            {
                SetStatus(f.Device, DriveStatus.Of(51)); // OVERFLOW IN RECORD: the rest is dropped
                continue;
            }
            f.RelOut.Add(b);
            if (b == 13) CommitRelRecord(f, terminated: true);
        }
        f.RelData = null;
    }

    /// <summary>Stores what was written into the current record and moves on to the next one.</summary>
    void CommitRelRecord(BasicFile f, bool terminated)
    {
        if (f.Rel == null || f.RelOut.Count == 0) return;
        int length = f.Rel.RecordLength;
        var record = new byte[length];
        try
        {
            var old = f.Rel.Read(f.RelRecord);
            // writing in the middle of a record keeps what is in front of it; a fresh record is empty
            if (old[0] != 0xFF) Array.Copy(old, record, length);
        }
        catch (DriveException) { }

        for (int i = 0; i < f.RelOut.Count; i++) record[f.RelPos + i] = f.RelOut[i];
        if (terminated) // everything after the CR is nulled
            for (int i = f.RelPos + f.RelOut.Count; i < length; i++) record[i] = 0;

        try
        {
            f.Rel.Write(f.RelRecord, record); // a status from the write (51, overflow) stays until it is read
        }
        catch (DriveException e) { SetStatus(f.Device, DriveStatus.Of(e.Code, e.Track, e.Sector)); }

        f.RelOut.Clear();
        f.RelRecord++;
        f.RelPos = 0;
        f.RelData = null;
    }

    /// <summary>The P command: where the next record is read or written.</summary>
    DriveStatus? PositionRel(int channel, int record, int position)
    {
        var f = _files.Values.FirstOrDefault(x => x.Rel != null && x.Secondary == channel);
        if (f == null) return null;

        CommitRelRecord(f, terminated: false); // a half-written record is kept as it is
        f.RelOut.Clear();
        f.RelRecord = Math.Max(record, 1) - 1;
        f.RelPos = Math.Max(position, 1) - 1;
        f.RelData = null;
        f.RelPendingCr = false;
        return f.RelRecord >= f.Rel!.RecordCount ? DriveStatus.Of(50) : DriveStatus.Ok;
    }
}
