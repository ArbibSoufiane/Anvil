namespace ArbiPache.App.Services;

/// <summary>Whether this Windows user trusts ArbiPache's local certificate authority, so browsers show a padlock.</summary>
public interface ILocalCaTrust
{
    bool IsTrusted();

    /// <summary>Windows asks the user to confirm.</summary>
    void Trust();

    void Untrust();
}
