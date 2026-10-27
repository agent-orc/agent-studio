using System.Runtime.CompilerServices;
using System.Text.Json;
using AgentStudio.Git;

namespace AgentStudio.Tests;

/// <summary>
/// Runs every backend host of this test process with the strict Git read guard
/// (AGT-3321, decision D6): a GET request that starts Git without a frozen
/// exception, or past its route's ceiling, throws and fails the test that sent
/// it.
///
/// <para>Census mode for measuring existing tests: with
/// <c>GIT_READ_GUARD_LEDGER_OUT=&lt;file&gt;</c> the guard only warns, and the
/// per-route ledger is written to that file as JSON when the process exits.
/// The file is evidence for a reviewed manifest change, never an input that
/// expands the manifest.</para>
/// </summary>
internal static class GitReadGuardBootstrap
{
    [ModuleInitializer]
    internal static void StrictReadGuard()
    {
        var ledgerOut = Environment.GetEnvironmentVariable("GIT_READ_GUARD_LEDGER_OUT");
        if (string.IsNullOrWhiteSpace(ledgerOut))
        {
            GitReadRequestGuard.Mode = GitReadGuardMode.Strict;
            return;
        }
        GitReadRequestGuard.Mode = GitReadGuardMode.Warn;
        AppDomain.CurrentDomain.ProcessExit += (_, _) => File.WriteAllText(ledgerOut,
            JsonSerializer.Serialize(GitReadRequestGuard.Ledger.Snapshot(),
                new JsonSerializerOptions { WriteIndented = true }));
    }
}
