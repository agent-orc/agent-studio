using AgentStudio.Operations.Server;

if (args is ["bootstrap", var principals, var agent, var client])
{
    AgentStudio.Operations.Server.Features.Access.OperationsBootstrap.Run(principals, agent, client);
    return;
}

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 32768);
builder.Services.AddOperationsServer(builder.Configuration);
var app = builder.Build();
app.MapOperationsServer();
await app.RunAsync();
