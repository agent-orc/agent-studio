using System.Net;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// The connector cases whose subject is the environment of the launching
/// process. They write process environment variables, which every host
/// booting at the same time would read, so they run in the
/// <see cref="ProcessEnvironmentSerialCollection"/>; the rest of the connector
/// suite boots in parallel (AGT-3106).
/// </summary>
[Collection(ProcessEnvironmentSerialCollection.Name)]
public sealed class ConnectorLaunchEnvironmentTests
{
    [Theory]
    [InlineData("ASPNETCORE_URLS", "http://127.0.0.1:5031")]
    [InlineData("URLS", "http://127.0.0.1:5031")]
    [InlineData("Kestrel__Endpoints__Http__Url", "http://127.0.0.1:5031")]
    public async Task Host_boots_while_the_launching_process_owns_a_listener(string variable, string value)
    {
        // The gate condition: the Studio backend starts `dotnet test`, so the
        // test process inherits the listener configuration of a host that is
        // already serving on the connector's port. Every host-booting test
        // then failed inside the gate with "The connector may listen only on
        // http://[::1]:5031" while passing from an operator shell (AGT-2840).
        // The test bootstrap removes that configuration once per process, so
        // the suite no longer depends on its launcher.
        var previous = Environment.GetEnvironmentVariable(variable);
        Environment.SetEnvironmentVariable(variable, value);
        try
        {
            Assert.ThrowsAny<Exception>(() => ConnectorUnderTest.Boot(new RecordingTransport()));

            var removed = LauncherListenerEnvironment.Strip(
                Environment.GetEnvironmentVariables(),
                name => Environment.SetEnvironmentVariable(name, null));

            Assert.Equal([variable], removed);
            await using var connector = ConnectorUnderTest.Boot(new RecordingTransport());
            using var liveness = await connector.Client.GetAsync("/healthz");
            Assert.Equal(HttpStatusCode.OK, liveness.StatusCode);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, previous);
        }
    }

    [Fact]
    public void Credential_supplied_through_an_environment_variable_stops_the_boot()
    {
        const string variable = "Connector__BearerToken";
        var previous = Environment.GetEnvironmentVariable(variable);
        Environment.SetEnvironmentVariable(variable, "env-studio-secret");
        try
        {
            var exception = Assert.ThrowsAny<Exception>(() =>
                ConnectorUnderTest.Boot(new RecordingTransport()));
            var message = Flatten(exception);
            Assert.Contains("Connector:BearerToken", message, StringComparison.Ordinal);
            Assert.DoesNotContain("env-studio-secret", message, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, previous);
        }
    }

    private static string Flatten(Exception exception)
    {
        var messages = new List<string>();
        for (Exception? current = exception; current is not null; current = current.InnerException)
            messages.Add(current.Message);
        return string.Join(" | ", messages);
    }
}
