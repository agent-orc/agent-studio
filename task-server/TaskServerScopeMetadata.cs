using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.TaskServer;

public sealed record TaskServerScopeMetadata(string Scope);

public sealed record TaskServerPrincipal(
    string PrincipalId,
    string Kind,
    IReadOnlySet<string> Scopes,
    string? RunnerId);

public static class TaskServerScopeExtensions
{
    public static RouteGroupBuilder RequireTaskServerScope(
        this RouteGroupBuilder builder,
        string scope)
        => builder.WithMetadata(new TaskServerScopeMetadata(scope));

    public static RouteHandlerBuilder RequireTaskServerScope(
        this RouteHandlerBuilder builder,
        string scope)
        => builder.WithMetadata(new TaskServerScopeMetadata(scope));

    public static TBuilder RequireTaskServerScope<TBuilder>(
        this TBuilder builder,
        string scope)
        where TBuilder : IEndpointConventionBuilder
    {
        builder.Add(endpoint => endpoint.Metadata.Add(new TaskServerScopeMetadata(scope)));
        return builder;
    }

    public static TaskServerPrincipal? TaskServerPrincipal(this HttpContext context)
        => context.Items.TryGetValue(typeof(TaskServerPrincipal), out var value)
            ? value as TaskServerPrincipal
            : null;
}
