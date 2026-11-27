using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace AgentStudio.Security;

/// <summary>
/// Terminal sign-in confirmed with step-up (Dossier operator-elevated-session,
/// slice ES-3, decision ES-D2 option S2). <c>studio-op login</c> starts a
/// sign-in anonymously with the public half of its device key; an active
/// Owner confirms it in Studio with a fresh second factor, and the terminal's
/// next poll receives an <c>oas.*</c> session of the chosen operator agent.
/// The record keeps hashes of the user code and the poll handle, never the
/// values.
/// </summary>
public sealed record OperatorSignIn
{
    public required string Id { get; init; }
    /// <summary>SHA-256 of the normalized eight-character user code.</summary>
    public required string UserCodeHash { get; init; }
    /// <summary>SHA-256 of the poll handle the terminal holds.</summary>
    public required string PollHandleHash { get; init; }
    public required OperatorDevicePublicKey PublicKey { get; init; }
    /// <summary>RFC 7638 SHA-256 thumbprint of <see cref="PublicKey"/>, base64url.</summary>
    public required string KeyThumbprint { get; init; }
    public string BindingKind { get; init; } = "memory";
    public string? DeviceKeyThumbprint { get; init; }
    public OperatorDevicePublicKey? DevicePublicKey { get; init; }
    public bool CertificationAttempted { get; init; }
    public int? RequestedLifetimeHours { get; init; }
    public required string DeviceLabel { get; init; }
    /// <summary>The operator agent the terminal asked for, by id or name, as it sent it.</summary>
    public string? RequestedAgent { get; init; }
    public List<string> RequestedClasses { get; init; } = [];
    /// <summary>The client address that started the sign-in, as the trusted edge resolved it.</summary>
    public string? RequestedAddress { get; init; }
    public DateTime CreatedAt { get; init; }
    /// <summary>Ten minutes after the start; the code and an uncollected session end here.</summary>
    public DateTime ExpiresAt { get; init; }
    public string State { get; init; } = OperatorSignInStates.Pending;
    public DateTime? DecidedAt { get; init; }
    /// <summary>The Owner who confirmed or denied.</summary>
    public string? DecidedBy { get; init; }
    public string? OperatorAgentId { get; init; }
    /// <summary>The session created at confirmation; its bearer is minted at the next poll.</summary>
    public string? SessionId { get; init; }
    public DateTime? IssuedAt { get; init; }
}

public static class OperatorSignInStates
{
    public const string Pending = "pending";
    public const string Confirmed = "confirmed";
    public const string Issued = "issued";
    public const string Denied = "denied";
    public const string Expired = "expired";
}

/// <summary>The answers a poll gives, as the terminal reads them.</summary>
public static class OperatorSignInPollStatuses
{
    public const string Pending = "pending";
    public const string SlowDown = "slow-down";
    public const string Denied = "denied";
    public const string Expired = "expired";
    public const string Session = "session";
}

/// <summary>An EC P-256 public key as a JWK (RFC 7517); no private member is accepted.</summary>
public sealed record OperatorDevicePublicKey(string Kty, string Crv, string X, string Y)
{
    /// <summary>RFC 7638: SHA-256 over the required members in lexicographic order, base64url without padding.</summary>
    public string Thumbprint()
    {
        var canonical = $"{{\"crv\":\"{Crv}\",\"kty\":\"{Kty}\",\"x\":\"{X}\",\"y\":\"{Y}\"}}";
        return Base64Url(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    internal static string Base64Url(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>
/// The device a sign-in came from, recorded on the session it created: the
/// label the terminal sent, the key thumbprint enforced by ES-4, the requesting address and the Owner's address and browser
/// session at confirmation.
/// </summary>
public sealed record OperatorSessionDevice
{
    public required string DeviceId { get; init; }
    public required string Label { get; init; }
    public required string KeyThumbprint { get; init; }
    public string? RequestedAddress { get; init; }
    public string? ConfirmedAddress { get; init; }
    /// <summary>Revoking this browser session from a session list ends the operator session.</summary>
    public required string ConfirmingSessionId { get; init; }
    public string BindingKind { get; init; } = "memory";
    public string? DeviceKeyThumbprint { get; init; }
    public OperatorDevicePublicKey? DevicePublicKey { get; init; }
    public required string SignInId { get; init; }
}

/// <summary>A known device of one operator agent: a key thumbprint the Owner confirmed at least once.</summary>
public sealed record OperatorDevice
{
    public required string Id { get; init; }
    public required string KeyThumbprint { get; init; }
    public required string Label { get; init; }
    public string BindingKind { get; init; } = "memory";
    public DateTime FirstConfirmedAt { get; init; }
    public DateTime LastConfirmedAt { get; init; }
    public string? LastConfirmedBy { get; init; }
    public string? LastAddress { get; init; }
}

/// <summary>Eight characters of the RFC 8628 base-20 consonant alphabet, shown as two groups of four.</summary>
public static class OperatorSignInCodes
{
    public const string Alphabet = "BCDFGHJKLMNPQRSTVWXZ";
    public const int Length = 8;

    public static string New()
    {
        Span<char> code = stackalloc char[Length];
        for (var index = 0; index < Length; index++) code[index] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        return new string(code);
    }

    /// <summary>Upper case without separators or spaces; null when the input cannot be a code.</summary>
    public static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 32) return null;
        var builder = new StringBuilder(Length);
        foreach (var character in value)
        {
            if (character is '-' or ' ') continue;
            var upper = char.ToUpperInvariant(character);
            if (!Alphabet.Contains(upper)) return null;
            builder.Append(upper);
        }
        return builder.Length == Length ? builder.ToString() : null;
    }

    public static string Display(string code) => code.Length == Length ? $"{code[..4]}-{code[4..]}" : code;

    public static string Hash(string normalized) => PasswordSecretHasher.HashSecret("operator-sign-in-code:" + normalized);
}

public sealed record StartOperatorSignInRequest(
    OperatorDevicePublicKey? PublicKey, string? DeviceLabel, string? OperatorAgent, IReadOnlyList<string>? Classes, int? LifetimeHours = null);

public sealed record StartOperatorSignInResponse(
    string SignInId, string UserCode, string VerificationUri, string VerificationUriComplete, string PollHandle,
    int Interval, DateTime ExpiresAt, string KeyThumbprint);

/// <summary>ES256 signature in IEEE P1363 format over the domain-separated certification statement.</summary>
public sealed record OperatorDeviceCertification(OperatorDevicePublicKey PublicKey, string BindingKind, long IssuedAt, string Signature);

public sealed record PollOperatorSignInRequest(string? PollHandle, OperatorDeviceCertification? Certification = null);

/// <summary>A poll answer. The session fields are set only with <see cref="OperatorSignInPollStatuses.Session"/>.</summary>
public sealed record PollOperatorSignInResponse(
    string Status, int Interval, string? Message = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Token = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? OperatorAgentId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? SessionId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Actor = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? DelegatedBy = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTime? ExpiresAt = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? Classes = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? KeyThumbprint = null, string BindingKind = "memory", DateTime? ChosenEnd = null, DateTime? KeyExpiresAt = null, DateTime? IdleEnd = null);

public sealed record OperatorSignInCodeRequest(string? Code);

public sealed record ConfirmOperatorSignInRequest(
    string? Code, string? OperatorAgentId, IReadOnlyList<string>? Classes, int? LifetimeHours);

public sealed record OperatorSignInAgentOption(string Id, string Name, IReadOnlyList<int>? LifetimeHours = null, int DefaultLifetimeHours = 8);

public sealed record OperatorSignInClassResponse(string Id, string Title, string Mode);

/// <summary>What the confirmation page shows for an entered code.</summary>
public sealed record OperatorSignInLookupResponse(
    string SignInId, string UserCode, string State, string DeviceLabel, string KeyThumbprint,
    IReadOnlyList<string> KnownForAgentIds, string? RequestedAgent, string? RequestedAgentId,
    IReadOnlyList<OperatorSignInClassResponse> RequestedClasses, string? RequestedAddress, string? BrowserAddress,
    bool AddressMatches, DateTime CreatedAt, DateTime ExpiresAt, IReadOnlyList<int> LifetimeHours, int DefaultLifetimeHours,
    IReadOnlyList<OperatorSignInAgentOption> Agents, string BindingKind = "memory", string? DeviceKeyThumbprint = null);

public sealed record ConfirmOperatorSignInResponse(
    string SignInId, string OperatorAgentId, string SessionId, string Actor, DateTime ExpiresAt, IReadOnlyList<string> Classes, DateTime? ChosenEnd = null, int? ChosenLifetimeHours = null);

public sealed record OperatorSessionDeviceResponse(
    string DeviceId, string Label, string KeyThumbprint, string? RequestedAddress, string? ConfirmedAddress, string SignInId, string BindingKind = "memory", string? DeviceKeyThumbprint = null);

public sealed record OperatorDeviceResponse(
    string Id, string Label, string KeyThumbprint, DateTime FirstConfirmedAt, DateTime LastConfirmedAt, string? LastConfirmedBy,
    string? LastAddress, DateTime? LastUsedAt, int ActiveSessions, string BindingKind = "memory");
