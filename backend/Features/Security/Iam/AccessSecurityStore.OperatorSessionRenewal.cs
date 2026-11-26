using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace AgentStudio.Security;

public sealed record OperatorSessionRenewalRequest(string SessionId, OperatorDevicePublicKey NewPublicKey,
    long IssuedAt, string Signature);

public sealed partial class AccessSecurityStore
{
    public static string OperatorRenewalStatement(string sessionId, string thumbprint, long issuedAt)
        => $"studio-op-session-renewal-v1\n{sessionId}\n{thumbprint}\n{issuedAt.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>Rotates a certified session key without extending the chosen lifetime or idle clock.</summary>
    public OperatorSessionResponse RenewBoundOperatorSession(string? bearer, OperatorSessionRenewalRequest request,
        HttpRequest proof, string? address)
    {
        lock (_gate)
        {
            var next = Copy(StateLocked());
            var agent = next.ServiceAccounts.FirstOrDefault(account => IsOperatorAgent(account)
                && account.Sessions?.Any(session => session.Id == request.SessionId) == true);
            var session = agent?.Sessions!.First(candidate => candidate.Id == request.SessionId);
            if (OperatorKillRequested || agent is null || agent.DisabledAt is not null || session is null
                || SessionEnded(next, session, Now) || !ConfirmingPersonHasFactor(next, session)
                || !ActiveOwnerIds(next.Users, next.RoleBindings, next.Groups).Contains(session.CreatedBy)
                || !PasswordSecretHasher.VerifySecret(bearer ?? "", session.SecretHash))
                throw new SecurityOperationException(401, "session-ended", "The session ended. Run studio-op login.");
            using var actorScope = AgentStudio.Shared.OperatorActorScope.Enter(
                new AgentStudio.Shared.OperatorActor(agent.Id, session.Id, session.CreatedBy));
            var device = session.Device;
            if (device is null || agent.Devices?.Any(known => known.Id == device.DeviceId) != true
                || device.DevicePublicKey is null)
                throw new SecurityOperationException(401, "renewal-device-key-missing", "The device key is missing. Run studio-op login.");
            if (EffectiveSessionLimit(next, agent, device.BindingKind) is { } max && Now >= session.CreatedAt.AddHours(max))
            {
                next.ServiceAccounts[next.ServiceAccounts.IndexOf(agent)] = agent with
                {
                    Sessions = agent.Sessions!.Select(candidate => candidate.Id == session.Id
                        ? candidate with { RevokedAt = Now, RevokedBy = "session-limit" } : candidate).ToList(),
                };
                var cancelled = CancelOpenApprovalsLocked(next, approval => approval.SessionId == session.Id, Now, "session limit lowered");
                SaveLocked(next);
                AppendIamAudit(session.CreatedBy, "operator-session.revoke", "operator-agent", agent.Id,
                    new { sessionId = session.Id, reason = "limit-lowered" });
                AuditCancelledApprovals(session.CreatedBy, cancelled);
                throw new SecurityOperationException(401, "limit-lowered", "A session limit was lowered. Run studio-op login.");
            }
            var refusal = proof.HttpContext.Items["OperatorSession.ProofThumbprint"] as string == device.KeyThumbprint
                ? null : ValidateOperatorProof(proof, bearer!, device.KeyThumbprint);
            if (refusal is not null)
                throw new SecurityOperationException(401, refusal, "The current session key must prove renewal. Run studio-op login.");
            var key = ValidateDevicePublicKey(request.NewPublicKey);
            var seconds = _clock.GetUtcNow().ToUnixTimeSeconds();
            if (request.IssuedAt < seconds - 60 || request.IssuedAt > seconds + 60
                || key.Thumbprint() == device.KeyThumbprint || !ValidRenewalSignature(device.DevicePublicKey, request))
                throw new SecurityOperationException(401, "renewal-device-key-missing", "Device renewal proof failed. Run studio-op login.");
            var newAddress = address is not null && !session.UsedAddresses.Contains(address);
            var renewed = session with
            {
                Device = device with { KeyThumbprint = key.Thumbprint() }, KeyExpiresAt = Now.AddMinutes(60), RenewedAt = Now,
                LastAddress = address ?? session.LastAddress, UsedAddresses = RememberAddress(session.UsedAddresses, address),
                NewAddressAt = newAddress ? Now : session.NewAddressAt,
            };
            next.ServiceAccounts[next.ServiceAccounts.IndexOf(agent)] = agent with
            {
                Sessions = agent.Sessions!.Select(candidate => candidate.Id == session.Id ? renewed : candidate).ToList(),
            };
            SaveLocked(next);
            AppendIamAudit(null, "operator-session.renew", "operator-agent", agent.Id,
                new { sessionId = session.Id, device.BindingKind, address, newAddress, renewed.KeyExpiresAt }, session.Id);
            return ToOperatorSessionResponse(agent.Id, renewed);
        }
    }

    private static bool ValidRenewalSignature(OperatorDevicePublicKey deviceKey, OperatorSessionRenewalRequest request)
    {
        try
        {
            using var verifier = ECDsa.Create(new ECParameters
            {
                Curve = ECCurve.NamedCurves.nistP256,
                Q = new ECPoint { X = DecodeCoordinate(deviceKey.X), Y = DecodeCoordinate(deviceKey.Y) },
            });
            if (request.Signature is null || request.Signature.Length != 86) return false;
            var signature = Convert.FromBase64String(request.Signature.Replace('-', '+').Replace('_', '/') + "==");
            return verifier.VerifyData(Encoding.UTF8.GetBytes(OperatorRenewalStatement(request.SessionId,
                request.NewPublicKey.Thumbprint(), request.IssuedAt)), signature, HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch (Exception error) when (error is CryptographicException or FormatException or ArgumentException) { return false; }
    }
}
