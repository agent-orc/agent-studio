namespace AgentRunner;

/// <summary>
/// <c>zone-policy remove</c> (AGT-3231): the rollback of an apply. It deletes
/// the jumps into the zone chains (IPv4 and IPv6, including the IPv6
/// <c>FORWARD</c> fallback) and then the two chains themselves. Docker
/// networks, ipsets and the zone resolver stay; the runbook's rollback section
/// removes them once no container needs them. Like apply, every step reads
/// first, so a second run changes nothing, and without <c>execute</c> nothing
/// is changed at all.
/// </summary>
public sealed class ZonePolicyRemover(IZoneHost host)
{
    /// <summary>Each jump was inserted once; more copies come only from manual edits.</summary>
    private const int MaxJumpCopies = 8;

    private static readonly (string Family, string Chain, string Target)[] Jumps =
    [
        (ZonePolicyRules.Ipv4, ZonePolicyRules.DockerUserChain, ZonePolicyRules.ForwardChain),
        (ZonePolicyRules.Ipv4, ZonePolicyRules.InputBuiltInChain, ZonePolicyRules.InputChain),
        (ZonePolicyRules.Ipv6, ZonePolicyRules.DockerUserChain, ZonePolicyRules.ForwardChain),
        (ZonePolicyRules.Ipv6, ZonePolicyRules.ForwardBuiltInChain, ZonePolicyRules.ForwardChain),
        (ZonePolicyRules.Ipv6, ZonePolicyRules.InputBuiltInChain, ZonePolicyRules.InputChain),
    ];

    public async Task<ZoneApplyReport> RemoveAsync(bool execute, CancellationToken ct)
    {
        var steps = new List<ZoneApplyStep>();
        // A chain cannot be deleted while a rule still jumps to it.
        foreach (var (family, chain, target) in Jumps)
            steps.Add(await RemoveJumpAsync(ZonePolicyRules.Iptables(family), chain, target, execute, ct));
        if (steps.Any(step => step.Failed)) return new ZoneApplyReport(execute, steps);
        foreach (var family in new[] { ZonePolicyRules.Ipv4, ZonePolicyRules.Ipv6 })
        {
            foreach (var chain in new[] { ZonePolicyRules.ForwardChain, ZonePolicyRules.InputChain })
                steps.Add(await RemoveChainAsync(ZonePolicyRules.Iptables(family), chain, execute, ct));
        }
        return new ZoneApplyReport(execute, steps);
    }

    private async Task<ZoneApplyStep> RemoveJumpAsync(
        string executable, string chain, string target, bool execute, CancellationToken ct)
    {
        var name = $"{executable} {chain} -j {target}";
        var removed = 0;
        while (true)
        {
            var check = await host.RunAsync(executable, ["-C", chain, "-j", target], null, ct);
            if (!check.Succeeded)
            {
                if (ZonePolicyApplier.IsPermissionOrNoTool(check)) return ZonePolicyApplier.Unchecked(name, check, execute);
                // No such rule, or no such parent chain (the IPv6 DOCKER-USER
                // without Docker's ip6tables support): nothing to delete.
                return removed == 0 ? new ZoneApplyStep(name, "unchanged") : new ZoneApplyStep(name, "deleted", Copies(removed));
            }
            if (!execute) return new ZoneApplyStep(name, "would-delete");
            if (removed == MaxJumpCopies)
                return new ZoneApplyStep(name, "failed", $"still present after deleting {Copies(removed)}");
            var delete = await host.RunAsync(executable, ["-D", chain, "-j", target], null, ct);
            if (!delete.Succeeded)
                return new ZoneApplyStep(name, "failed", ZonePolicyApplier.FirstLine(delete.Error, $"exit {delete.ExitCode}"));
            removed++;
        }
    }

    private async Task<ZoneApplyStep> RemoveChainAsync(string executable, string chain, bool execute, CancellationToken ct)
    {
        var name = $"{executable} chain {chain}";
        var exists = await host.RunAsync(executable, ["-S", chain], null, ct);
        if (!exists.Succeeded)
            return ZonePolicyApplier.IsPermissionOrNoTool(exists)
                ? ZonePolicyApplier.Unchecked(name, exists, execute)
                : new ZoneApplyStep(name, "unchanged", "not present");
        if (!execute) return new ZoneApplyStep(name, "would-delete");
        foreach (var args in new string[][] { ["-F", chain], ["-X", chain] })
        {
            var result = await host.RunAsync(executable, args, null, ct);
            if (!result.Succeeded)
                return new ZoneApplyStep(name, "failed", ZonePolicyApplier.FirstLine(result.Error, $"exit {result.ExitCode}"));
        }
        return new ZoneApplyStep(name, "deleted");
    }

    private static string Copies(int count) => count == 1 ? "1 copy" : $"{count} copies";
}
