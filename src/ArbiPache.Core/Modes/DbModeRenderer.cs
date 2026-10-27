using System.Globalization;
using System.Text;
using ArbiPache.Core.Database;

namespace ArbiPache.Core.Modes;

/// <summary>
/// data\conf\db-mode.cnf: the mode's database values. my.ini includes it last, so these values win. The profile's
/// engine-neutral statement_timeout (seconds) becomes MariaDB's max_statement_time (seconds) or MySQL's
/// max_execution_time (milliseconds).
/// </summary>
public static class DbModeRenderer
{
    public static string Render(ModeProfile p, DbEngine e)
    {
        var text = new StringBuilder()
            .Append($"# Written by ArbiPache for {p.Mode} mode. my.ini includes it last, so these values win.\r\n")
            .Append("# Change them on the Dev / Production page: this file is rewritten.\r\n")
            .Append("[mysqld]\r\n");
        foreach (var (key, value) in p.Database)
        {
            if ((key + value).Any(char.IsControl))
                throw new ArgumentException($"The {p.Mode} profile's database value for {key} cannot contain line breaks.");
            var line = OptionNames.Comparer.Equals(key, "statement_timeout") ? StatementTimeout(p, e, value) : $"{key} = {value}";
            text.Append(line).Append("\r\n");
        }
        return text.ToString();
    }

    private static string StatementTimeout(ModeProfile p, DbEngine e, string value)
    {
        if (!double.TryParse(value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var seconds))
            throw new ArgumentException($"The {p.Mode} profile's statement_timeout must be a number of seconds, not \"{value}\".");
        return e == DbEngine.MariaDb
            ? $"max_statement_time = {seconds.ToString(CultureInfo.InvariantCulture)}"
            : $"max_execution_time = {Math.Round(seconds * 1000).ToString(CultureInfo.InvariantCulture)}";
    }
}
