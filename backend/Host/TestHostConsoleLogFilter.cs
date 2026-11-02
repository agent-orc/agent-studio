using Serilog.Events;

/// <summary>
/// Keeps in-process test hosts from filling verification stdout with expected
/// fixture activity. Their file logger still records every event. Other
/// warnings and all errors remain visible when a test fails.
/// </summary>
internal static class TestHostConsoleLogFilter
{
    public static bool ShouldWrite(LogEvent logEvent)
    {
        if (logEvent.Level < LogEventLevel.Warning)
            return false;
        if (logEvent.Level >= LogEventLevel.Error)
            return true;

        var template = logEvent.MessageTemplate.Text;
        if (template.StartsWith("result-scaffold-created ", StringComparison.Ordinal))
            return false;
        if (template.StartsWith("git-index-run-failed ", StringComparison.Ordinal)
            && logEvent.Exception is DirectoryNotFoundException)
            return false;
        if (logEvent.RenderMessage().Contains(
                "agent-studio test guard: the real ", StringComparison.Ordinal))
            return false;
        return true;
    }
}
