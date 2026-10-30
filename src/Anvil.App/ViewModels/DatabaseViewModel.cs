using Anvil.App.Services;
using CommunityToolkit.Mvvm.Input;

namespace Anvil.App.ViewModels;

/// <summary>Where to download a database server, for the guide on the Database page.</summary>
public sealed record DbDownload(string Name, string Url, string Note);

/// <summary>
/// The Database page: the MariaDB and MySQL servers the user installed and linked, and a short guide to getting one. Anvil
/// does not download a database itself.
/// </summary>
public sealed partial class DatabaseViewModel(LinkedDatabasesViewModel linked, Action<string>? openUrl = null)
{
    private readonly Action<string> _openUrl = openUrl ?? EditorLauncher.OpenUrl;

    public static IReadOnlyList<DbDownload> Downloads { get; } =
    [
        new("MariaDB", "https://mariadb.org/download/", "Choose Windows, x86_64 and the MSI package, or the ZIP file for a plain folder."),
        new("MySQL", "https://dev.mysql.com/downloads/mysql/", "MySQL Community Server: the MSI installer, or the ZIP archive for a plain folder."),
    ];

    /// <summary>The guide's steps, in order.</summary>
    public static IReadOnlyList<string> Steps { get; } =
    [
        "Download MariaDB or MySQL below. The MSI installer sets the server up as a Windows service with a root password.",
        "From a ZIP file instead, unzip it and create its data folder once: mariadb-install-db.exe --password=root for MariaDB, " +
        "or mysqld --initialize-insecure for MySQL (root then has no password).",
        "Press Link a server… and choose the server's bin folder, such as C:\\Program Files\\MariaDB 11.8\\bin, " +
        "then enter the user and password Anvil logs in with.",
        "Your PHP sites connect to 127.0.0.1 on the port shown next to the server.",
    ];

    /// <summary>"Your servers": MariaDB and MySQL servers the user installed themselves.</summary>
    public LinkedDatabasesViewModel Linked { get; } = linked;

    public Task LoadAsync(CancellationToken ct = default) => Linked.LoadAsync(ct);

    [RelayCommand]
    private void OpenDownload(DbDownload download) => _openUrl(download.Url);
}
