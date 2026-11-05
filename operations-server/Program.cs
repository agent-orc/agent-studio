using AgentStudio.Operations.Server;

if (args is ["bootstrap", var principals, var agent, var client])
{
    AgentStudio.Operations.Server.Features.Access.OperationsBootstrap.Run(principals, agent, client);
    return;
}

// Security model SM-2, out of band: revokes operator principals in the
// principal file and cancels their operations when the control plane cannot
// reach this server. Without operator agent ids it revokes every principal
// that names an operator agent.
if (args is ["revoke-operators", var principalsFile, var dataDirectory, .. var operatorAgentIds])
{
    var revoked = await AgentStudio.Operations.Server.Features.Access.OperatorRevocationCommand.RunAsync(
        principalsFile, dataDirectory, operatorAgentIds);
    Console.WriteLine(revoked.RevokedPrincipalIds.Length == 0
        ? "No operator principal matched."
        : $"Revoked operator principals: {string.Join(", ", revoked.RevokedPrincipalIds)}; cancelled operations: {revoked.CancelledAttempts}");
    return;
}

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 32768);
builder.Services.AddOperationsServer(builder.Configuration);
var app = builder.Build();
app.MapOperationsServer();
await app.RunAsync();
