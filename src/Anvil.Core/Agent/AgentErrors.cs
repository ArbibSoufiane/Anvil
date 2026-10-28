using System.Text.Json;
using Anvil.Core.Certificates;
using StreamJsonRpc;

namespace Anvil.Core.Agent;

/// <summary>Errors that keep their meaning across the Agent pipe, where only a message and a code survive.</summary>
public static class AgentErrors
{
    /// <summary>JSON-RPC error code of a certificate the Agent refused; the error data is the <see cref="CertificateImportException.Code"/>.</summary>
    public const int CertificateImport = 1001;

    /// <summary>Agent side: runs <paramref name="call"/>, sending a refused certificate to the GUI with its code.</summary>
    public static async Task<T> CertificateCall<T>(Func<Task<T>> call)
    {
        try
        {
            return await call();
        }
        catch (CertificateImportException e)
        {
            throw new LocalRpcException(e.Message) { ErrorCode = CertificateImport, ErrorData = e.Code };
        }
    }

    /// <summary>GUI side: the certificate problem's code, whether the error came over the pipe or from the same process; else null.</summary>
    public static string? CertificateCode(Exception e) => e switch
    {
        CertificateImportException c => c.Code,
        RemoteInvocationException { ErrorCode: CertificateImport } r => r.ErrorData switch
        {
            string code => code,
            JsonElement { ValueKind: JsonValueKind.String } code => code.GetString(),
            _ => r.DeserializedErrorData as string,
        },
        _ => null,
    };
}
