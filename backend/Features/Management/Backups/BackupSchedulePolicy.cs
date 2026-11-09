namespace AgentStudio.Management;

/// <summary>When the scheduler creates, verifies and uploads. Pure: the hosted service supplies the clock.</summary>
public static class BackupSchedulePolicy
{
    /// <summary>A failed run is retried after this long, or after the interval when that is shorter.</summary>
    public static readonly TimeSpan RetryAfterFailure = TimeSpan.FromMinutes(30);

    public static DateTimeOffset? NextBackupAt(BackupOptions options, BackupState state, BackupRecord? newest)
    {
        if (!options.Configured) return null;
        var byInterval = newest is null ? DateTimeOffset.MinValue : Parse(newest.CreatedAt) + options.Interval;
        if (state.LastRun is { Outcome: "failed" } failed)
        {
            var retry = Parse(failed.At) + (options.Interval < RetryAfterFailure ? options.Interval : RetryAfterFailure);
            return retry > byInterval ? retry : byInterval;
        }
        return byInterval;
    }

    public static bool BackupDue(DateTimeOffset now, BackupOptions options, BackupState state, BackupRecord? newest)
        => NextBackupAt(options, state, newest) is { } next && now >= next;

    /// <summary>
    /// The newest good backup is verified once per verification interval, and
    /// at once when the last verification failed and a newer backup exists.
    /// </summary>
    public static bool VerifyDue(DateTimeOffset now, BackupOptions options, BackupState state, BackupRecord? newestGood)
    {
        if (!options.Configured || newestGood is null) return false;
        if (state.LastVerification is not { } last) return true;
        if (last.Outcome == "failed" && last.BackupId != newestGood.Id) return true;
        return now >= Parse(last.At) + options.VerifyInterval;
    }

    /// <summary>Backoff for a failed upload: 15 minutes, doubling, at most six hours.</summary>
    public static bool UploadDue(DateTimeOffset now, BackupUploadRecord? upload)
    {
        if (upload is null || upload.State == "pending" && upload.LastAttemptAt is null) return true;
        if (upload.State == "uploaded") return false;
        if (upload.LastAttemptAt is null) return true;
        var minutes = Math.Min(15 * Math.Pow(2, Math.Max(0, upload.Attempts - 1)), 360);
        return now >= Parse(upload.LastAttemptAt) + TimeSpan.FromMinutes(minutes);
    }

    public static DateTimeOffset Parse(string value)
        => DateTimeOffset.Parse(value, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal);
}
