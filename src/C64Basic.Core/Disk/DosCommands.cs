namespace C64Basic.Core.Disk;

/// <summary>The disk drive command channel (secondary address 15): S, R, N, V, I, C and the direct-access block commands.</summary>
public static class DosCommands
{
    /// <summary>Runs one command such as <c>S0:OLD</c> or <c>R:NEW=OLD</c> and returns the status the drive would report.</summary>
    public static DriveStatus Execute(IDiskDrive drive, string command, IBlockChannels? channels = null)
    {
        command = command.Trim('\r', '\n', ' ');
        if (command.Length == 0) return DriveStatus.Ok;
        try
        {
            if (BlockCommand(drive, command, channels) is { } blockResult) return blockResult;
            int colon = command.IndexOf(':');
            string verb = (colon >= 0 ? command[..colon] : command).Trim().ToUpperInvariant();
            string args = colon >= 0 ? command[(colon + 1)..] : "";

            // "S0", "R0", "N0" and "SCRATCH0" all name the command; the digit is the drive number
            switch (verb.TrimEnd('0', '1'))
            {
                case "S" or "SCRATCH":
                    {
                        if (colon < 0) return DriveStatus.Of(34);
                        int count = 0;
                        foreach (var pattern in args.Split(','))
                            count += drive.Scratch(StripDriveNumber(pattern));
                        return DriveStatus.Scratched(count);
                    }
                case "R" or "RENAME":
                    {
                        int eq = args.IndexOf('=');
                        if (colon < 0 || eq < 0) return DriveStatus.Of(34);
                        drive.Rename(args[(eq + 1)..].Trim(), args[..eq].Trim());
                        return DriveStatus.Ok;
                    }
                case "N" or "NEW":
                    {
                        if (colon < 0) return DriveStatus.Of(34);
                        var parts = args.Split(',');
                        drive.Format(parts[0], parts.Length > 1 ? parts[1] : null);
                        return DriveStatus.Ok;
                    }
                case "V" or "VALIDATE":
                    drive.Validate();
                    return DriveStatus.Ok;
                case "I" or "INITIALIZE":
                    return DriveStatus.Ok;
                case "C" or "COPY":
                    {
                        int eq = args.IndexOf('=');
                        if (colon < 0 || eq < 0) return DriveStatus.Of(34);
                        var data = new List<byte>();
                        var type = FileType.Seq;
                        bool first = true;
                        foreach (var source in args[(eq + 1)..].Split(','))
                        {
                            var file = drive.Read(StripDriveNumber(source));
                            if (first) { type = file.Type; data.AddRange(file.Data); first = false; }
                            else data.AddRange(type == FileType.Prg ? file.Data.Skip(2) : file.Data);
                        }
                        drive.Write(args[..eq].Trim(), type, data.ToArray(), false);
                        return DriveStatus.Ok;
                    }
                default:
                    return DriveStatus.Of(31);
            }
        }
        catch (DriveException e)
        {
            return DriveStatus.Of(e.Code, e.Track, e.Sector);
        }
    }

    static readonly string[] BlockVerbs = { "U1", "UA", "U2", "UB", "B-R", "B-W", "B-P", "B-A", "B-F" };

    /// <summary>
    /// The direct-access commands: U1/UA and B-R read a block into a channel's buffer, U2/UB and B-W write it back, B-P moves the
    /// buffer pointer, B-A and B-F allocate and free a block in the allocation map. Returns null for any other command.
    /// </summary>
    static DriveStatus? BlockCommand(IDiskDrive drive, string command, IBlockChannels? channels)
    {
        string upper = command.ToUpperInvariant();
        string? verb = BlockVerbs.FirstOrDefault(v => upper.StartsWith(v));
        if (verb == null) return null;

        var parts = command[verb.Length..].Split(new[] { ' ', ',', ':' }, StringSplitOptions.RemoveEmptyEntries);
        var n = new int[parts.Length];
        for (int i = 0; i < parts.Length; i++)
            if (!int.TryParse(parts[i], out n[i])) return DriveStatus.Of(30);

        switch (verb)
        {
            case "U1" or "UA" or "B-R" or "U2" or "UB" or "B-W":
                {
                    if (n.Length < 4) return DriveStatus.Of(30);
                    int channel = n[0], track = n[2], sector = n[3];
                    var buffer = channels?.Buffer(channel) ?? throw new DriveException(70);
                    if (verb is "U1" or "UA" or "B-R")
                    {
                        drive.ReadBlock(track, sector).CopyTo(buffer, 0);
                        channels!.SetPointer(channel, 0);
                    }
                    else drive.WriteBlock(track, sector, buffer);
                    return DriveStatus.Ok;
                }
            case "B-P":
                {
                    if (n.Length < 2 || n[1] is < 0 or > 255) return DriveStatus.Of(30);
                    if (channels?.Buffer(n[0]) == null) throw new DriveException(70);
                    channels.SetPointer(n[0], n[1]);
                    return DriveStatus.Ok;
                }
            default: // B-A, B-F
                {
                    if (n.Length < 3) return DriveStatus.Of(30);
                    if (verb == "B-A") drive.AllocateBlock(n[1], n[2]); else drive.FreeBlock(n[1], n[2]);
                    return DriveStatus.Ok;
                }
        }
    }

    /// <summary>Removes a leading "0:" drive number from a file spec.</summary>
    public static string StripDriveNumber(string spec)
    {
        spec = spec.Trim();
        return spec.Length >= 2 && spec[1] == ':' && char.IsAsciiDigit(spec[0]) ? spec[2..] : spec;
    }
}
