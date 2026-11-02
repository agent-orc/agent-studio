using System.Net;
using AgentRunner;
using Xunit;

namespace AgentRunner.Tests;

/// <summary>SM-9: a host warns once a day in the last week of its runner credential.</summary>
public sealed class CredentialExpiryWarningTests
{
    private static readonly DateTime Now = new(2026, 11, 2, 9, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(8.0, null, false)]
    [InlineData(7.0, null, true)]
    [InlineData(2.0, null, true)]
    [InlineData(-1.0, null, true)]
    [InlineData(3.0, 23.0, false)]
    [InlineData(3.0, 24.0, true)]
    public void Warns_inside_the_last_week_at_most_once_a_day(double daysLeft, double? hoursSinceWarning, bool expected)
    {
        DateTime? last = hoursSinceWarning is { } hours ? Now.AddHours(-hours) : null;

        Assert.Equal(expected, CredentialExpiryWarning.ShouldWarn(Now.AddDays(daysLeft), Now, last));
    }

    [Fact]
    public void The_message_names_the_end_and_what_to_do()
    {
        Assert.Contains("in 3 day(s)", CredentialExpiryWarning.Message(Now.AddDays(2.5), Now), StringComparison.Ordinal);
        Assert.Contains("expired at", CredentialExpiryWarning.Message(Now.AddDays(-1), Now), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_handler_reads_the_server_header_and_warns_once()
    {
        var warnings = new List<string>();
        var expires = DateTime.UtcNow.AddDays(3).ToString("O", System.Globalization.CultureInfo.InvariantCulture);
        using var client = new HttpClient(new CredentialExpiryWarningHandler(new Answer(expires), warnings.Add, TimeProvider.System))
        {
            BaseAddress = new Uri("http://127.0.0.1"),
        };

        await client.GetAsync("/api/v1/artifact-limits");
        await client.GetAsync("/api/v1/artifact-limits");

        Assert.Contains("expires at", Assert.Single(warnings), StringComparison.Ordinal);
    }

    private sealed class Answer(string expires) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK);
            response.Headers.TryAddWithoutValidation(CredentialExpiryWarningHandler.Header, expires);
            return Task.FromResult(response);
        }
    }
}
