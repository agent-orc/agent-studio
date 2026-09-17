using System.Text.Json;

namespace AgentStudio.Diagnostics;

/// <summary>
/// ADR-0031 phase-6 db-touch sentinel: a POST endpoint that round-trips
/// the request body. The Update Service's verifier hits it after a
/// restart to prove the .NET request pipeline is alive end-to-end (not
/// just /healthz which is a static literal).
///
/// The gate is <see cref="InternalProbeGate"/>: its own
/// <c>UpdateService:ProbeEnabled</c> switch, otherwise open wherever the
/// Stable update contract is installed, with <c>Environment:IsDev</c> and the
/// legacy <c>DevTools:UpdateStableEnabled</c> still accepted. AGT-2865: tying
/// the sentinel to the DevTools flag meant a default Stable could never pass
/// phase 6, and the run found out only after the restart. The endpoint is
/// registered unconditionally and returns 404 when the gate is closed, so
/// production callers cannot see it and the Update Service's preflight can
/// read that 404 as "gated off" before it touches the stack.
/// </summary>
public static class InternalProbeEndpoints
{
    public static void MapInternalProbeEndpoints(this WebApplication app)
    {
        app.MapPost("/api/_internal/probe", async (HttpContext ctx, IConfiguration config) =>
        {
            var gate = InternalProbeGate.Decide(config);
            if (!gate.Enabled) return Results.NotFound();

            // Read the body, deserialise as JsonElement so we can echo any shape,
            // and return it back wrapped with a server-side timestamp.
            JsonElement body;
            try
            {
                body = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
            }
            catch (JsonException ex)
            {
                return Results.Json(new { error = "invalid json", detail = ex.Message }, statusCode: 400);
            }

            return Results.Json(new
            {
                ok = true,
                receivedAt = DateTime.UtcNow,
                gate = gate.Reason.ToString(),
                echo = body
            });
        });
    }
}
