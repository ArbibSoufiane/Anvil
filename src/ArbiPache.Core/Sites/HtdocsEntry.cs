namespace ArbiPache.Core.Sites;

/// <summary>
/// One path under localhost/ and the machine's IP: a real folder in htdocs, or an alias to a folder elsewhere
/// (IsAlias), such as phpMyAdmin kept outside htdocs.
/// </summary>
public sealed record HtdocsEntry(string Folder, string Root, bool IsAlias, string? PhpVersion, AccessLevel Access);
