using AgentStudio.Security;
using AgentStudio.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentStudio.Tests;

public class ProductAcceptanceAttributionTests
{
    [Fact]
    public void NetworkedAcceptanceUsesTheSignedInPersonsName()
    {
        var now = new FakeTimeProvider().GetUtcNow().UtcDateTime;
        var context = new DefaultHttpContext();
        context.Request.Headers["X-Client-Id"] = "forged-client";
        context.Items[AccessSecurityMiddleware.HumanPrincipalItem] = new HumanPrincipal(
            new StudioUser
            {
                Id = "user-1", Username = "robert", DisplayName = "Robert Example",
                Role = StudioRoles.Owner, PasswordHash = "redacted",
            },
            new StudioSession
            {
                Id = "session", UserId = "user-1", TokenHash = "redacted", CsrfHash = "redacted",
                CreatedAt = now, LastSeenAt = now, ExpiresAt = now.AddHours(1),
                AbsoluteExpiresAt = now.AddHours(8),
            });

        Assert.Equal("human:Robert Example", TaskCrudEndpoints.OperatorActor(context));
    }
}
