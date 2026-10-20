using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentStudio.TaskServer;
using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace TaskServer.Tests;

public sealed class HostJoinTokenTests
{
    [Fact]
    public void Encodes_only_a_single_use_enrolment_and_pins_the_runner()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 10, 16, 0, 0, 0, TimeSpan.Zero));
        var enrolment = new IssuedEnrolment("enl_demo", "runner:host-a",
            TaskServerPrincipalKinds.Runner, "host-a", "ins_demo",
            "enr_" + new string('a', 64), clock.GetUtcNow().AddMinutes(15).UtcDateTime);
        var token = HostJoinTokenEncoder.Encode(enrolment, "https://tasks.example.test",
            "0.9.5", clock.GetUtcNow().UtcDateTime);
        var parts = token.Split('.');
        Assert.Equal("aosj2", parts[0]);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(parts[0] + "." + parts[1])))[..16].ToLowerInvariant(), parts[2]);
        var body = parts[1].Replace('-', '+').Replace('_', '/');
        body = body.PadRight(body.Length + (4 - body.Length % 4) % 4, '=');
        using var json = JsonDocument.Parse(Convert.FromBase64String(body));
        Assert.Equal("host-a", json.RootElement.GetProperty("runnerId").GetString());
        Assert.Equal(enrolment.EnrolmentCode,
            json.RootElement.GetProperty("enrolmentCode").GetString());
        Assert.Equal("", json.RootElement.GetProperty("credential").GetString());
    }
}
