

namespace AgentStudio.Tags;

/// <summary>
/// Workspace-level tag registry routes under <c>/api/tags</c>. Tags are a
/// flat namespace (not per-project) backed by <c>tags.json</c>; the FE
/// renders chips on cards by id and looks up label / colour from this
/// registry.
/// </summary>
public static class TagEndpoints
{
    public static void MapTagEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/tags");

        group.MapGet("/", (TagRegistryService tags) => Results.Ok(tags.GetAll()));

        group.MapPost("/", (CreateTagRequest req, TagRegistryService tags) =>
        {
            try
            {
                var entry = tags.Create(req.Id, req.Label, req.Color, req.Description, req.Kind);
                return Results.Ok(entry);
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(new { error = ex.Message });
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        group.MapDelete("/{id}", (string id, TagRegistryService tags) =>
        {
            try
            {
                return tags.Delete(id) ? Results.Ok() : Results.NotFound();
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(new { error = ex.Message });
            }
        });
    }
}
