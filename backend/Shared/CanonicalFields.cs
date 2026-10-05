using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace AgentStudio.Shared;

/// <summary>
/// Injective encoding for composite fingerprints and hashes (AGT-2989).
///
/// <para>
/// Joining fields with a delimiter is ambiguous as soon as a field can contain
/// that delimiter: <c>("a\nb", "c")</c> and <c>("a", "b\nc")</c> join to the
/// same text and therefore the same hash. Several of the joined fields are
/// caller-controlled (failure reasons, details, conflict paths), so a crafted
/// or merely unlucky value could alias a different fact set. Every field is
/// therefore written as <c>&lt;length&gt;:&lt;value&gt;;</c>, and a null field
/// as <c>~;</c>, behind a version prefix. Two different field lists never
/// share an encoding.
/// </para>
/// </summary>
public static class CanonicalFields
{
    private const string Version = "cf1;";

    /// <summary>Length-prefixed encoding of <paramref name="fields"/>; null and empty stay distinct.</summary>
    public static string Encode(params string?[] fields)
    {
        var builder = new StringBuilder(Version);
        foreach (var field in fields)
        {
            if (field is null)
            {
                builder.Append("~;");
                continue;
            }

            builder.Append(field.Length.ToString(CultureInfo.InvariantCulture))
                .Append(':')
                .Append(field)
                .Append(';');
        }

        return builder.ToString();
    }

    /// <summary>Lower-case SHA-256 hex of <see cref="Encode"/>.</summary>
    public static string Sha256Hex(params string?[] fields)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Encode(fields)))).ToLowerInvariant();

    /// <summary>
    /// Flattens a variable-length list into fields with its count first, so a
    /// list followed by more fields stays unambiguous.
    /// </summary>
    public static IEnumerable<string?> List(IReadOnlyCollection<string> items)
        => items.Prepend(items.Count.ToString(CultureInfo.InvariantCulture));
}
