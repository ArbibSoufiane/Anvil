using System.Globalization;
using System.Text;
using Anvil.Core.Database;

namespace Anvil.Core.Modes;

/// <summary>
/// data\&lt;engine&gt;\mode.cnf: the mode's database values. my.ini includes it last, so these values win. The profile's
/// engine-neutral statement_timeout (seconds) becomes MariaDB's max_statement_time (seconds) or MySQL's
/// max_execution_time (milliseconds).
/// </summary>
public static class DbModeRenderer
{
    public static string Render(ModeProfile p, DbEngine e)
    {
        var text = new StringBuilder()
            .Append($"# Written by Anvil for {p.Mode} mode. my.ini includes it last, so these values win.\r\n")
            .Append("# Change them on the Dev / Production page: this file is rewritten.\r\n")
            .Append("[mysqld]\r\n");
        foreach (var (key, value) in Values(p, e)) text.Append($"{key} = {value}\r\n");
        return text.ToString();
    }

    /// <summary>The options as the engine names them, in profile order.</summary>
    public static IReadOnlyList<KeyValuePair<string, string>> Values(ModeProfile p, DbEngine e) => p.Database.Select(pair =>
    {
        var (key, value) = pair;
        if ((key + value).Any(char.IsControl))
            throw new ArgumentException($"The {p.Mode} profile's database value for {key} cannot contain line breaks.");
        return OptionNames.Comparer.Equals(key, "statement_timeout") ? StatementTimeout(p, e, value) : pair;
    }).ToList();

    private static KeyValuePair<string, string> StatementTimeout(ModeProfile p, DbEngine e, string value)
    {
        if (!double.TryParse(value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var seconds))
            throw new ArgumentException($"The {p.Mode} profile's statement_timeout must be a number of seconds, not \"{value}\".");
        return e == DbEngine.MariaDb
            ? new("max_statement_time", seconds.ToString(CultureInfo.InvariantCulture))
            : new("max_execution_time", Math.Round(seconds * 1000).ToString(CultureInfo.InvariantCulture));
    }
}
