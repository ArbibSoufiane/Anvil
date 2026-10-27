using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Security.WinTrust;

namespace ArbiPache.Core.Update;

/// <summary>Whether a file carries a valid Authenticode signature from a given publisher.</summary>
public static class AuthenticodeCheck
{
    /// <summary>
    /// True when Windows accepts the file's embedded signature (intact file, trusted chain) and the signing certificate's
    /// name is <paramref name="publisher"/>. Revocation is not checked, so the check also works offline.
    /// </summary>
    public static bool IsSignedBy(string path, string publisher)
    {
        string? signer;
        try
        {
#pragma warning disable SYSLIB0057 // Reads the signer of a signed file; X509CertificateLoader has no equivalent.
            using var certificate = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
#pragma warning restore SYSLIB0057
            signer = certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false);
        }
        catch (CryptographicException)
        {
            return false;
        }
        return string.Equals(signer, publisher, StringComparison.Ordinal) && Verified(path);
    }

    private static unsafe bool Verified(string path)
    {
        fixed (char* file = path)
        {
            var fileInfo = new WINTRUST_FILE_INFO { cbStruct = (uint)sizeof(WINTRUST_FILE_INFO), pcwszFilePath = file };
            var data = new WINTRUST_DATA
            {
                cbStruct = (uint)sizeof(WINTRUST_DATA),
                dwUIChoice = WINTRUST_DATA_UICHOICE.WTD_UI_NONE,
                fdwRevocationChecks = WINTRUST_DATA_REVOCATION_CHECKS.WTD_REVOKE_NONE,
                dwUnionChoice = WINTRUST_DATA_UNION_CHOICE.WTD_CHOICE_FILE,
                dwStateAction = WINTRUST_DATA_STATE_ACTION.WTD_STATEACTION_VERIFY,
            };
            data.Anonymous.pFile = &fileInfo;
            var action = PInvoke.WINTRUST_ACTION_GENERIC_VERIFY_V2;
            var result = PInvoke.WinVerifyTrust(HWND.Null, &action, &data);
            data.dwStateAction = WINTRUST_DATA_STATE_ACTION.WTD_STATEACTION_CLOSE;
            PInvoke.WinVerifyTrust(HWND.Null, &action, &data);
            return result == 0;
        }
    }
}
