using System.Text.Json;
using ArbiPache.Core.Database;
using ArbiPache.Core.IO;
using ArbiPache.Core.Paths;
using ArbiPache.Core.Settings;

namespace ArbiPache.Core.Modes;

/// <summary>The user's changes to one built-in profile; a null part keeps the built-in value.</summary>
public sealed record ModeProfileOverrides(
    IReadOnlyDictionary<string, string>? Php,
    IReadOnlyDictionary<string, string>? Apache,
    IReadOnlyDictionary<string, string>? Database,
    bool? Xdebug, bool? SecurityHeaders, bool? DirectoryListing);

/// <summary>
/// The Development and Production profiles: the built-in values with the user's overrides from
/// data\profiles\&lt;mode&gt;.json on top. A file that cannot be read is moved aside, never overwritten. Values end up
/// in php.ini, Apache and my.ini files one per line, so a value with a line break is never saved or used.
/// </summary>
public sealed class ProfileStore(AppPaths paths)
{
    public string? LastLoadWarning { get; private set; }

    public string FileFor(ServerMode m) => Path.Combine(paths.Profiles, $"{m.ToString().ToLowerInvariant()}.json");

    public ModeProfile Get(ServerMode m)
    {
        LastLoadWarning = null;
        var builtIn = BuiltInProfiles.For(m);
        if (Load(m) is not { } o) return builtIn;
        var ignored = new List<string>();
        var profile = builtIn with
        {
            // Each part keys the way its config reads it: php.ini as written, Apache directives
            // without case, MariaDB/MySQL options with - and _ alike.
            Php = Merge(builtIn.Php, o.Php, StringComparer.Ordinal, ignored),
            Apache = Merge(builtIn.Apache, o.Apache, StringComparer.OrdinalIgnoreCase, ignored),
            Database = Merge(builtIn.Database, o.Database, OptionNames.Comparer, ignored),
            Xdebug = o.Xdebug ?? builtIn.Xdebug,
            SecurityHeaders = o.SecurityHeaders ?? builtIn.SecurityHeaders,
            DirectoryListing = o.DirectoryListing ?? builtIn.DirectoryListing,
        };
        if (ignored.Count > 0)
            LastLoadWarning = $"{Path.GetFileName(FileFor(m))}: {string.Join(", ", ignored)} {(ignored.Count == 1 ? "contains a line break and was" : "contain line breaks and were")} ignored.";
        return profile;
    }

    public void SaveOverrides(ServerMode m, ModeProfileOverrides o)
    {
        foreach (var (key, value) in new[] { o.Php, o.Apache, o.Database }.SelectMany(part => part ?? new Dictionary<string, string>()))
            if (HasLineBreak(key, value)) throw new ArgumentException($"{key} cannot contain line breaks.");
        Directory.CreateDirectory(paths.Profiles);
        JsonFiles.Write(FileFor(m), o);
    }

    public void Reset(ServerMode m) => File.Delete(FileFor(m));

    private ModeProfileOverrides? Load(ServerMode m)
    {
        var file = FileFor(m);
        if (!File.Exists(file)) return null;
        try
        {
            return JsonFiles.Read<ModeProfileOverrides>(file);
        }
        catch (JsonException ex)
        {
            var aside = $"{file}.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}";
            File.Move(file, aside, overwrite: true);
            LastLoadWarning = $"{Path.GetFileName(file)} could not be read ({ex.Message}). It was saved as {Path.GetFileName(aside)} and the built-in {m} profile is in use.";
            return null;
        }
    }

    private static Dictionary<string, string> Merge(IReadOnlyDictionary<string, string> builtIn, IReadOnlyDictionary<string, string>? overrides,
        IEqualityComparer<string> comparer, List<string> ignored)
    {
        var merged = new Dictionary<string, string>(builtIn, comparer);
        foreach (var (key, value) in overrides ?? new Dictionary<string, string>())
        {
            if (HasLineBreak(key, value)) ignored.Add(key);
            else merged[key] = value;
        }
        return merged;
    }

    private static bool HasLineBreak(string key, string value) => key.Any(char.IsControl) || value.Any(char.IsControl);
}
