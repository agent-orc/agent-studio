using System.Security.Cryptography;

namespace AgentStudio.TestSupport;

/// <summary>
/// Deadline-bounded polling helpers shared by process-topology harnesses. Every
/// loop re-checks the watched process is still alive on each iteration, so a
/// crashed child fails fast with its captured output instead of waiting out the
/// full timeout.
/// </summary>
public static class ProcessWaiters
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);

    public static async Task WaitForHttpAsync(string url, ManagedProcess process, TimeSpan? timeout = null)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        var deadline = DateTime.UtcNow.Add(timeout ?? TimeSpan.FromSeconds(20));
        Exception? last = null;
        while (DateTime.UtcNow < deadline)
        {
            process.EnsureRunning();
            try
            {
                using var response = await client.GetAsync(url);
                if (response.IsSuccessStatusCode) return;
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
            {
                last = exception;
            }
            await Task.Delay(PollInterval);
        }
        throw new TimeoutException(
            $"Process did not become ready at {url}: {last?.Message}{Environment.NewLine}{process}");
    }

    public static async Task WaitForHttpsAsync(
        string url,
        ManagedProcess process,
        string expectedCertificateSha256,
        TimeSpan? timeout = null)
    {
        using var handler = PinnedHandler(expectedCertificateSha256);
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(2) };
        var deadline = DateTime.UtcNow.Add(timeout ?? TimeSpan.FromSeconds(20));
        Exception? last = null;
        while (DateTime.UtcNow < deadline)
        {
            process.EnsureRunning();
            try
            {
                using var response = await client.GetAsync(url);
                if (response.IsSuccessStatusCode) return;
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
            {
                last = exception;
            }
            await Task.Delay(PollInterval);
        }
        throw new TimeoutException(
            $"HTTPS process did not become ready at {url}: {last?.Message}{Environment.NewLine}{process}");
    }

    public static System.Net.Http.HttpClientHandler PinnedHandler(string expectedCertificateSha256)
        => new()
        {
            ServerCertificateCustomValidationCallback = (_, certificate, _, _) =>
                certificate is not null
                && string.Equals(
                    Convert.ToHexString(SHA256.HashData(certificate.RawData)),
                    expectedCertificateSha256,
                    StringComparison.OrdinalIgnoreCase),
        };

    public static async Task WaitForFileAsync(string path, ManagedProcess process, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow.Add(timeout);
        while (DateTime.UtcNow < deadline)
        {
            process.EnsureRunning();
            if (File.Exists(path)) return;
            await Task.Delay(50);
        }
        throw new TimeoutException($"Process did not create '{path}'.{Environment.NewLine}{process}");
    }

    public static async Task WaitForOutputAsync(ManagedProcess process, string expected, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow.Add(timeout);
        while (DateTime.UtcNow < deadline)
        {
            process.EnsureRunning();
            if (process.Contains(expected)) return;
            await Task.Delay(PollInterval);
        }
        throw new TimeoutException($"Process output did not contain '{expected}'.{Environment.NewLine}{process}");
    }

    /// <summary>Polls an arbitrary async condition (e.g. a JSON GET matching a predicate) on a fixed interval.</summary>
    public static async Task WaitForConditionAsync(
        Func<Task<bool>> probe,
        ManagedProcess process,
        TimeSpan timeout,
        string description)
    {
        var deadline = DateTime.UtcNow.Add(timeout);
        while (DateTime.UtcNow < deadline)
        {
            process.EnsureRunning();
            if (await probe()) return;
            await Task.Delay(PollInterval);
        }
        throw new TimeoutException($"Condition not met: {description}.{Environment.NewLine}{process}");
    }
}
