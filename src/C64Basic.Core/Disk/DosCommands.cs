namespace C64Basic.Core.Disk;

/// <summary>The disk drive's command channel (secondary address 15): S, R, N, V, I and C commands.</summary>
public static class DosCommands
{
    /// <summary>Runs one command such as <c>S0:OLD</c> or <c>R:NEW=OLD</c> and returns the status the drive would report.</summary>
    public static DriveStatus Execute(IDiskDrive drive, string command)
    {
        command = command.Trim('\r', '\n', ' ');
        if (command.Length == 0) return DriveStatus.Ok;
        try
        {
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

    /// <summary>Removes a leading "0:" drive number from a file spec.</summary>
    public static string StripDriveNumber(string spec)
    {
        spec = spec.Trim();
        return spec.Length >= 2 && spec[1] == ':' && char.IsAsciiDigit(spec[0]) ? spec[2..] : spec;
    }
}
