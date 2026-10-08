using System.Globalization;
using System.Text;

namespace C64Basic.Core.Runtime;

public readonly struct Value
{
    public readonly double N;
    public readonly string? S;

    Value(double n, string? s) { N = n; S = s; }

    public bool IsStr => S != null;
    public static Value Num(double n) => new(n, null);
    public static Value Str(string s) => new(0, s);
    public static readonly Value Zero = Num(0);
    public static readonly Value Empty = Str("");
}

public sealed class BasicArray
{
    public int[] Bounds { get; }
    public Value[] Data { get; }

    public BasicArray(int[] bounds, Value fill)
    {
        Bounds = bounds;
        long size = 1;
        foreach (var b in bounds) size *= b + 1;
        Data = new Value[size];
        Array.Fill(Data, fill);
    }

    public int Offset(int[] idx)
    {
        if (idx.Length != Bounds.Length) throw new BasicException(ErrorCode.BadSubscript);
        int off = 0;
        for (int i = 0; i < idx.Length; i++)
        {
            if (idx[i] < 0) throw new BasicException(ErrorCode.IllegalQuantity);
            if (idx[i] > Bounds[i]) throw new BasicException(ErrorCode.BadSubscript);
            off = off * (Bounds[i] + 1) + idx[i];
        }
        return off;
    }
}

/// <summary>Formats numbers the way PRINT and STR$ do on a C64.</summary>
public static class NumberFormat
{
    /// <summary>Returns the number with a leading space (or minus sign), without PRINT's trailing space.</summary>
    public static string Format(double d)
    {
        if (d == 0) return " 0";
        string sign = d < 0 ? "-" : " ";
        string e = Math.Abs(d).ToString("E8", CultureInfo.InvariantCulture); // d.dddddddd E+xxx
        int ePos = e.IndexOf('E');
        string digits = (e[0] + e.Substring(2, ePos - 2)).TrimEnd('0');
        if (digits.Length == 0) digits = "0";
        int exp = int.Parse(e.AsSpan(ePos + 1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
        int n = digits.Length;

        if (exp >= -2 && exp <= 8)
        {
            if (exp >= 0)
            {
                if (n <= exp + 1) return sign + digits + new string('0', exp + 1 - n);
                return sign + digits[..(exp + 1)] + "." + digits[(exp + 1)..];
            }
            return sign + "." + new string('0', -exp - 1) + digits;
        }

        var sb = new StringBuilder(sign);
        sb.Append(digits[0]);
        if (n > 1) sb.Append('.').Append(digits, 1, n - 1);
        sb.Append('E').Append(exp < 0 ? '-' : '+').Append(Math.Abs(exp).ToString("00", CultureInfo.InvariantCulture));
        return sb.ToString();
    }
}

public static class NumberParser
{
    // Scans [+-]digits[.digits][E[+-]digits] from `start`; returns the end index.
    static int Scan(string s, int start, out bool hasDigits)
    {
        int i = start, n = s.Length;
        hasDigits = false;
        if (i < n && (s[i] == '+' || s[i] == '-')) i++;
        while (i < n && char.IsAsciiDigit(s[i])) { i++; hasDigits = true; }
        if (i < n && s[i] == '.')
        {
            i++;
            while (i < n && char.IsAsciiDigit(s[i])) { i++; hasDigits = true; }
        }
        if (hasDigits && i < n && (s[i] == 'E' || s[i] == 'e'))
        {
            int j = i + 1;
            if (j < n && (s[j] == '+' || s[j] == '-')) j++;
            if (j < n && char.IsAsciiDigit(s[j]))
            {
                while (j < n && char.IsAsciiDigit(s[j])) j++;
                i = j;
            }
        }
        return i;
    }

    /// <summary>VAL semantics: parse a leading number, ignore the rest, 0 if there isn't one.</summary>
    public static double ParsePrefix(string s)
    {
        int start = 0;
        while (start < s.Length && s[start] == ' ') start++;
        int end = Scan(s, start, out bool has);
        if (!has) return 0;
        return double.Parse(s.AsSpan(start, end - start), NumberStyles.Float, CultureInfo.InvariantCulture);
    }

    /// <summary>INPUT / READ semantics: the whole text must be a number.</summary>
    public static bool TryParseFull(string s, out double d)
    {
        s = s.Trim();
        d = 0;
        int end = Scan(s, 0, out bool has);
        if (!has || end != s.Length) return false;
        d = double.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);
        return true;
    }
}
