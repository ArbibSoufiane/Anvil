using System.Text.RegularExpressions;

namespace Anvil.Core.IO;

/// <summary>
/// Edits php.ini-style files in place. Every line keeps its exact bytes unless an edit targets it,
/// so the user's comments and layout survive. Keys and sections compare case-insensitively.
/// </summary>
public sealed partial class IniDocument
{
    private readonly List<Line> _lines = [];
    private string _newline = "\n";

    private sealed class Line(string text, string ending)
    {
        public string Text { get; set; } = text;
        public string Ending { get; set; } = ending;
        public string Section { get; init; } = "";
    }

    public static IniDocument Parse(string text)
    {
        var doc = new IniDocument();
        if (text.Contains("\r\n", StringComparison.Ordinal)) doc._newline = "\r\n";
        var section = "";
        var start = 0;
        while (start < text.Length)
        {
            var end = text.IndexOf('\n', start);
            var (body, ending) = end < 0
                ? (text[start..], "")
                : end > start && text[end - 1] == '\r' ? (text[start..(end - 1)], "\r\n") : (text[start..end], "\n");
            var header = Header().Match(body);
            if (header.Success) section = header.Groups["name"].Value.Trim();
            doc._lines.Add(new Line(body, ending) { Section = section });
            start = end < 0 ? text.Length : end + 1;
        }
        return doc;
    }

    /// <summary>The last active value; PHP applies the last one too.</summary>
    public string? Get(string section, string key) =>
        Active(section, key).Select(l => Unquote(Pair().Match(l.Text).Groups["value"].Value)).LastOrDefault();

    public IReadOnlyList<string> GetAll(string section, string key) =>
        Active(section, key).Select(l => Unquote(Pair().Match(l.Text).Groups["value"].Value)).ToList();

    /// <summary>Replaces the active line, else uncomments ";key = ...", else appends to the section.</summary>
    public void Set(string section, string key, string value)
    {
        var text = $"{key} = {value}";
        var target = Active(section, key).LastOrDefault() ?? Commented(section, key, null).LastOrDefault();
        if (target is not null) target.Text = text;
        else Insert(section, text, after: null);
    }

    /// <summary>Adds one value of a multi-valued key such as <c>extension=</c>; a commented-out copy is uncommented in place.</summary>
    public void AddValue(string section, string key, string value)
    {
        if (Active(section, key).Any(l => SameValue(l, value))) return;
        var commented = Commented(section, key, value).FirstOrDefault();
        if (commented is not null)
        {
            commented.Text = $"{key}={value}";
            return;
        }
        var last = _lines.LastOrDefault(l => InSection(l, section) && (IsKey(Pair(), l, key) || IsKey(CommentedPair(), l, key)));
        Insert(section, $"{key}={value}", after: last);
    }

    /// <summary>Comments the value's line out with ";".</summary>
    public void RemoveValue(string section, string key, string value)
    {
        foreach (var line in Active(section, key).Where(l => SameValue(l, value)).ToList())
            line.Text = ";" + line.Text.TrimStart();
    }

    /// <summary>Deletes every active line of the key in the section.</summary>
    public void Remove(string section, string key) => _lines.RemoveAll(l => InSection(l, section) && IsKey(Pair(), l, key));

    /// <summary>The section's active keys and values in file order.</summary>
    public IReadOnlyList<(string Key, string Value)> Pairs(string section) =>
        _lines.Where(l => InSection(l, section)).Select(l => Pair().Match(l.Text)).Where(m => m.Success)
            .Select(m => (m.Groups["key"].Value, Unquote(m.Groups["value"].Value))).ToList();

    /// <summary>Each section name once, in file order.</summary>
    public IReadOnlyList<string> Sections() =>
        _lines.Select(l => l.Section).Where(s => s.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    public override string ToString() => string.Concat(_lines.Select(l => l.Text + l.Ending));

    private IEnumerable<Line> Active(string section, string key) =>
        _lines.Where(l => InSection(l, section) && IsKey(Pair(), l, key));

    private IEnumerable<Line> Commented(string section, string key, string? value) =>
        _lines.Where(l => InSection(l, section) && IsKey(CommentedPair(), l, key)
            && (value is null || string.Equals(Unquote(CommentedPair().Match(l.Text).Groups["value"].Value), value, StringComparison.OrdinalIgnoreCase)));

    private void Insert(string section, string text, Line? after)
    {
        var index = after is not null ? _lines.IndexOf(after) : _lines.FindLastIndex(l => InSection(l, section) && l.Text.Trim().Length > 0);
        if (index < 0)
        {
            AppendLine($"[{section}]", section);
            AppendLine(text, section);
            return;
        }
        var anchor = _lines[index];
        if (anchor.Ending.Length == 0) anchor.Ending = _newline;
        _lines.Insert(index + 1, new Line(text, _newline) { Section = anchor.Section });
    }

    private void AppendLine(string text, string section)
    {
        if (_lines.Count > 0 && _lines[^1].Ending.Length == 0) _lines[^1].Ending = _newline;
        _lines.Add(new Line(text, _newline) { Section = section });
    }

    private static bool InSection(Line line, string section) =>
        string.Equals(line.Section, section, StringComparison.OrdinalIgnoreCase);

    private static bool IsKey(Regex pattern, Line line, string key)
    {
        var m = pattern.Match(line.Text);
        return m.Success && string.Equals(m.Groups["key"].Value, key, StringComparison.OrdinalIgnoreCase);
    }

    private static bool SameValue(Line line, string value) =>
        string.Equals(Unquote(Pair().Match(line.Text).Groups["value"].Value), value, StringComparison.OrdinalIgnoreCase);

    private static string Unquote(string value)
    {
        value = value.Trim();
        return value.Length >= 2 && value[0] == '"' && value[^1] == '"' ? value[1..^1] : value;
    }

    [GeneratedRegex(@"^\s*\[(?<name>[^\]]+)\]\s*$")]
    private static partial Regex Header();

    [GeneratedRegex(@"^\s*(?<key>[A-Za-z0-9_.\-]+)\s*=(?<value>.*)$")]
    private static partial Regex Pair();

    [GeneratedRegex(@"^\s*;\s*(?<key>[A-Za-z0-9_.\-]+)\s*=(?<value>.*)$")]
    private static partial Regex CommentedPair();
}
