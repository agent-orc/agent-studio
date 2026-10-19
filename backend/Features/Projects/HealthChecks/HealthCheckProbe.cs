using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace AgentStudio.Projects;

public interface IHealthCheckProbe
{
    Task<HealthSample> ProbeAsync(HealthCheckDefinition check, CancellationToken cancellationToken);
}

/// <summary>
/// Probes from the control plane. The timeout runs on the injected clock, and every
/// failure is reduced to a bounded error code; remote bodies are never stored.
/// </summary>
public sealed class NetworkHealthCheckProbe(IHttpClientFactory httpClients, TimeProvider clock) : IHealthCheckProbe
{
    public const string HttpClientName = "environment-health-checks";
    private const int BodyLimit = 64 * 1024;

    public async Task<HealthSample> ProbeAsync(HealthCheckDefinition check, CancellationToken cancellationToken)
    {
        var at = clock.GetUtcNow();
        var started = clock.GetTimestamp();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(check.TimeoutSeconds), clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        int Elapsed() => (int)Math.Round(clock.GetElapsedTime(started).TotalMilliseconds);
        try
        {
            return check.Kind switch
            {
                "http" => await ProbeHttpAsync(check, at, Elapsed, linked.Token),
                "tcp" => await ProbeTcpAsync(check, at, Elapsed, linked.Token),
                "tls" => await ProbeTlsAsync(check, at, Elapsed, linked.Token),
                _ => new(at, false, null, null, "unsupported-kind"),
            };
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return new(at, false, null, null, "timeout");
        }
        catch (HttpRequestException ex)
        {
            return new(at, false, null, null, ex.InnerException is AuthenticationException ? "tls-failed" : "connection-failed");
        }
        catch (SocketException ex)
        {
            return new(at, false, null, null, ex.SocketErrorCode == SocketError.HostNotFound ? "dns-failed" : "connection-failed");
        }
        catch (AuthenticationException)
        {
            return new(at, false, null, null, "tls-failed");
        }
        catch (IOException)
        {
            return new(at, false, null, null, "connection-failed");
        }
    }

    private async Task<HealthSample> ProbeHttpAsync(HealthCheckDefinition check, DateTimeOffset at,
        Func<int> elapsed, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, check.Url);
        using var response = await httpClients.CreateClient(HttpClientName)
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var status = (int)response.StatusCode;
        if (status != check.ExpectedStatus)
            return new(at, false, elapsed(), status, "unexpected-status");
        if (check.BodyContains is { } expected)
        {
            var body = await ReadBoundedAsync(response.Content, cancellationToken);
            if (!body.Contains(expected, StringComparison.Ordinal))
                return new(at, false, elapsed(), status, "body-mismatch");
        }
        return new(at, true, elapsed(), status, null);
    }

    private static async Task<HealthSample> ProbeTcpAsync(HealthCheckDefinition check, DateTimeOffset at,
        Func<int> elapsed, CancellationToken cancellationToken)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(check.Host!, check.Port!.Value, cancellationToken);
        return new(at, true, elapsed(), null, null);
    }

    private async Task<HealthSample> ProbeTlsAsync(HealthCheckDefinition check, DateTimeOffset at,
        Func<int> elapsed, CancellationToken cancellationToken)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(check.Host!, check.Port!.Value, cancellationToken);
        DateTimeOffset? notAfter = null;
        var errors = SslPolicyErrors.None;
        await using var stream = new SslStream(client.GetStream(), false);
        await stream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = check.Host,
            // Accept the handshake to read the certificate; the verdict below uses the errors.
            RemoteCertificateValidationCallback = (_, certificate, _, policyErrors) =>
            {
                if (certificate is X509Certificate2 typed) notAfter = new DateTimeOffset(typed.NotAfter.ToUniversalTime());
                errors = policyErrors;
                return true;
            },
        }, cancellationToken);
        var latency = elapsed();
        if (notAfter is { } expiry && expiry <= clock.GetUtcNow())
            return new(at, false, latency, null, "certificate-expired", notAfter);
        return errors == SslPolicyErrors.None
            ? new(at, true, latency, null, null, notAfter)
            : new(at, false, latency, null, "certificate-invalid", notAfter);
    }

    private static async Task<string> ReadBoundedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        var buffer = new byte[BodyLimit];
        var read = 0;
        while (read < buffer.Length)
        {
            var count = await stream.ReadAsync(buffer.AsMemory(read), cancellationToken);
            if (count == 0) break;
            read += count;
        }
        return Encoding.UTF8.GetString(buffer, 0, read);
    }
}
