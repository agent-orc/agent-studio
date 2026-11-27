namespace AgentStudio.Security;

public sealed partial class AccessSecurityStore
{
    public const string ElevationRateExceeded = "elevation-rate-exceeded";

    /// <summary>Checked after hard refusals and project bindings. Batch classes never admit single actions.</summary>
    public OperatorPolicyDecision? AdmitOperatorElevation(string agentId, string sessionId, OperatorOperation operation,
        string? project, string? reason)
    {
        lock (_gate)
        {
            var next = Copy(StateLocked());
            var agent = ActiveOperatorAgentLocked(next, agentId);
            var session = agent.Sessions?.FirstOrDefault(entry => entry.Id == sessionId);
            if (session is null || SessionEnded(next, session, Now) || session.Elevation is not { } elevation) return null;
            var entry = elevation.Classes.FirstOrDefault(candidate => ElevatedActionClasses.Find(candidate.Id) is { } catalog
                && catalog.Mode == ElevatedActionClasses.DirectAction && catalog.Operations.Contains(operation.Id));
            if (entry?.ArmedAt is not { } start || entry.ArmedUntil is not { } end || Now < start || Now >= end) return null;
            var catalog = ElevatedActionClasses.Find(entry.Id)!;
            if (catalog.PerProject && string.IsNullOrWhiteSpace(project)) return null;
            if (string.IsNullOrWhiteSpace(reason))
                return OperatorPolicyDecision.Refuse("elevation-reason-required", operation, "A stated reason is required for an elevated action.");
            var now = Now;
            var uses = entry.Uses.Where(use => use.At > now.AddHours(-1) && use.At <= now).ToList();
            OperatorPolicyDecision decision;
            OperatorAgentSession updated;
            if (uses.Count(use => !catalog.PerProject || string.Equals(use.Project, project, StringComparison.OrdinalIgnoreCase)) >= catalog.RatePerHour)
            {
                var refusals = elevation.RateRefusals.Where(at => at > now.AddMinutes(-10) && at <= now).Append(now).ToList();
                updated = refusals.Count >= 3
                    ? session with { Classes = [], Elevation = null, ElevationRateEndedAt = now }
                    : session with { Elevation = elevation with { RateRefusals = refusals } };
                decision = OperatorPolicyDecision.Refuse(ElevationRateExceeded, operation,
                    refusals.Count >= 3 ? "Elevation ended after three rate refusals in ten minutes. Review it in Needs you."
                        : "This elevated class has reached its hourly rate.");
                if (refusals.Count >= 3)
                    AppendIamAudit(session.CreatedBy, "operator-elevation.rate-ended", "operator-agent", agentId, new { sessionId, entry.Id });
            }
            else
            {
                var used = entry with { Uses = [.. uses, new(now, project)] };
                updated = session with { Elevation = elevation with { Classes = elevation.Classes.Select(item => item.Id == entry.Id ? used : item).ToList() } };
                decision = OperatorPolicyDecision.Admit(operation) with
                { ElevationClass = entry.Id, ArmingId = entry.ArmingId, ApprovedBy = entry.ConfirmedBy,
                    DeviceThumbprint = session.Device?.DeviceKeyThumbprint ?? session.Device?.DevicePublicKey?.Thumbprint() ?? session.Device?.KeyThumbprint };
            }
            next.ServiceAccounts[next.ServiceAccounts.IndexOf(agent)] = agent with
            { Sessions = agent.Sessions!.Select(item => item.Id == sessionId ? updated : item).ToList() };
            SaveLocked(next);
            return decision;
        }
    }

    public void RearmOperatorClass(string agentId, string sessionId, string classId, HumanPrincipal owner)
    {
        lock (_gate)
        {
            var next = Copy(StateLocked());
            RequireSignInOwnerLocked(next, owner);
            var agent = ActiveOperatorAgentLocked(next, agentId);
            var session = agent.Sessions?.FirstOrDefault(entry => entry.Id == sessionId)
                ?? throw new SecurityOperationException(404, "operator-session-not-found", "Operator session not found.");
            if (SessionEnded(next, session, Now))
                throw new SecurityOperationException(409, "session-ended", "Sign in again to start a new elevation.");
            var entry = session.Elevation?.Classes.FirstOrDefault(item => item.Id == classId);
            if (entry is null || ElevatedActionClasses.Find(classId)?.Mode != ElevatedActionClasses.DirectAction)
                throw new SecurityOperationException(400, "class-not-rearmable", "Only a confirmed direct-action class can be re-armed.");
            ConsumeStepUpLocked(next, owner, "Verify a second factor before re-arming an elevated class.");
            var armed = OperatorElevation.Arm(classId, owner.User.Id, Now, session.ExpiresAt) with { Uses = entry.Uses };
            var updated = session with { Elevation = session.Elevation! with
            { Classes = session.Elevation.Classes.Select(item => item.Id == classId ? armed : item).ToList() } };
            next.ServiceAccounts[next.ServiceAccounts.IndexOf(agent)] = agent with
            { Sessions = agent.Sessions!.Select(item => item.Id == sessionId ? updated : item).ToList() };
            SaveLocked(next);
            AppendIamAudit(owner.User.Id, "operator-elevation.rearm", "operator-agent", agentId,
                new { sessionId, classId, armed.ArmingId, armed.ArmedAt, armed.ArmedUntil }, owner.Session.Id);
        }
    }
}
