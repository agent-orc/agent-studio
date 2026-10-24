namespace AgentRunner;

/// <summary>Reject runner persistence outside the isolated test host root.</summary>
internal static class RunnerStateRootGuard
{
    internal const string TestRootVariable = "RUNNER_TEST_STATE_ROOT";

    internal static string Validate(string root, bool isImplicit = false, string? testRoot = null)
    {
        if (string.IsNullOrWhiteSpace(root))
            throw new ArgumentException("A runner state root is required.", nameof(root));

        var full = Path.GetFullPath(root);
        testRoot ??= Environment.GetEnvironmentVariable(TestRootVariable);
        if (!string.IsNullOrWhiteSpace(testRoot))
        {
            if (isImplicit)
                throw new InvalidOperationException("Runner tests must select an explicit temporary state root.");
            var relative = Path.GetRelativePath(Path.GetFullPath(testRoot), full);
            if (Path.IsPathRooted(relative) || relative == ".."
                || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                throw new InvalidOperationException("Runner tests cannot use a state root outside their temporary directory.");
        }

        return full;
    }
}
