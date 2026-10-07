namespace AgentRunner;

internal static class ProtectedHostPath
{
    public static void EnsureOutsideRepository(string path)
    {
        var directory = new DirectoryInfo(Path.GetFullPath(path));
        for (var current = directory; current is not null; current = current.Parent)
        {
            if (File.Exists(Path.Combine(current.FullName, ".git"))
                || Directory.Exists(Path.Combine(current.FullName, ".git")))
                throw new InvalidOperationException("Credential custody cannot be inside a Git checkout.");
        }
    }
}
