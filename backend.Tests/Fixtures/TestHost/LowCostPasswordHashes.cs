using System.Security.Cryptography;
using AgentStudio.Secrets;
using AgentStudio.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AgentStudio.Tests;

/// <summary>
/// Password hashes in the product's PBKDF2-SHA512 format with 1,000 iterations
/// instead of 600,000 (AGT-3861).
///
/// <para>A shared host takes the boot out of each test; what stays is the
/// hashing. Every bootstrap, invitation, acceptance, password change and
/// sign-in derives a key, about a second each on a loaded runner, and an
/// access test signs in several people. The encoded hash carries its
/// iteration count and <see cref="PasswordSecretHasher.VerifyPassword"/>
/// reads it, so a store built by <see cref="Store"/> still verifies every
/// password through the product code. A test of the hashing itself keeps
/// the product store.</para>
/// </summary>
internal static class LowCostPasswordHashes
{
    public const int Iterations = 1_000;

    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA512, 32);
        return $"pbkdf2-sha512${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    /// <summary>
    /// The access store the host would build, on <paramref name="clock"/> or
    /// the host's clock, hashing with <see cref="Hash"/>.
    /// </summary>
    public static AccessSecurityStore Store(IServiceProvider services, TimeProvider? clock = null)
        => new(services.GetRequiredService<IConfiguration>(),
            services.GetRequiredService<ILogger<AccessSecurityStore>>(),
            clock ?? services.GetService<TimeProvider>(),
            PasswordSecretHasher.VerifyPassword,
            Hash,
            services.GetRequiredService<IKeyEncryptionKeySource>());
}
