namespace ArbiPache.Core.Components;

/// <summary>Compares dotted versions segment by segment, numerically ("2.4.68" &gt; "2.4.9").</summary>
public static class SemVer
{
    public static int Compare(string a, string b)
    {
        var x = a.Split('.');
        var y = b.Split('.');
        for (var i = 0; i < Math.Min(x.Length, y.Length); i++)
        {
            var c = CompareSegment(x[i], y[i]);
            if (c != 0) return c;
        }
        // "8.4" sorts below "8.4.0".
        return x.Length.CompareTo(y.Length);
    }

    private static int CompareSegment(string a, string b)
    {
        var (na, ra) = Split(a);
        var (nb, rb) = Split(b);
        var c = na.CompareTo(nb);
        if (c != 0) return c;
        // A suffix marks a pre-release: "0RC1" sorts below "0".
        if (ra.Length == 0 || rb.Length == 0) return rb.Length.CompareTo(ra.Length);
        return string.Compare(ra, rb, StringComparison.OrdinalIgnoreCase);
    }

    private static (long Number, string Suffix) Split(string segment)
    {
        var digits = 0;
        while (digits < segment.Length && char.IsAsciiDigit(segment[digits])) digits++;
        var number = digits == 0 ? -1 : long.Parse(segment.AsSpan(0, Math.Min(digits, 18)));
        return (number, segment[digits..]);
    }
}
