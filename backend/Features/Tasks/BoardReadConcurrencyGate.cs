namespace AgentStudio.Tasks;

/// <summary>
/// Keeps overlapping board reads from building and serializing multiple full
/// task projections at once. Requests wait without scanning and can leave the
/// queue when their HTTP client disconnects.
/// </summary>
public sealed class BoardReadConcurrencyGate
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public IResult Wrap(Func<IResult> buildResponse) => new GatedResult(_gate, buildResponse);

    private sealed class GatedResult(SemaphoreSlim gate, Func<IResult> buildResponse) : IResult
    {
        public async Task ExecuteAsync(HttpContext context)
        {
            await gate.WaitAsync(context.RequestAborted);
            try
            {
                context.RequestAborted.ThrowIfCancellationRequested();
                var result = buildResponse();
                context.RequestAborted.ThrowIfCancellationRequested();
                // Results.Ok only serializes here, after the route handler has
                // returned. Releasing at the end of the factory would still
                // allow multiple full boards to occupy the heap together.
                await result.ExecuteAsync(context);
            }
            finally
            {
                gate.Release();
            }
        }
    }
}
