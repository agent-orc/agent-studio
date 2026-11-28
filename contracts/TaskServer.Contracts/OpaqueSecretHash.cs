using System.Security.Cryptography;
using System.Text;

namespace AgentStudio.TaskServer.Contracts;

/// <summary>Verifier for opaque secrets generated with a cryptographic random source.</summary>
public static class OpaqueSecretHash
{
    public static string Compute(string secret)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));
}
