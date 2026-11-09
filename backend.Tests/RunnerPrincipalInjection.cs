using AgentStudio.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

namespace AgentStudio.Tests;

/// <summary>
/// Attaches an authenticated runner credential to the requests under the given
/// path prefixes, as the networked access middleware does after it verified the
/// bearer. The local test host has no sign-in, so handlers otherwise never see one.
/// </summary>
internal sealed class RunnerPrincipalInjection(RunnerPrincipal principal, params string[] pathPrefixes) : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use((context, nextMiddleware) =>
        {
            if (pathPrefixes.Any(prefix => context.Request.Path.StartsWithSegments(prefix, StringComparison.OrdinalIgnoreCase)))
                context.Items[AccessSecurityMiddleware.RunnerPrincipalItem] = principal;
            return nextMiddleware(context);
        });
        next(app);
    };
}
