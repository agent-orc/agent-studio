namespace AgentStudio.Security;

/// <summary>Rejects oversized login bodies before JSON binding allocates them.</summary>
public sealed class LoginPayloadLimitMiddleware(RequestDelegate next)
{
    private const int MaxBytes = 2048;

    public async Task InvokeAsync(HttpContext context)
    {
        if (!HttpMethods.IsPost(context.Request.Method)
            || !string.Equals(context.Request.Path.Value?.TrimEnd('/'), "/api/auth/login", StringComparison.OrdinalIgnoreCase))
        {
            await next(context);
            return;
        }

        if (context.Request.ContentLength > MaxBytes)
        {
            context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            return;
        }

        var body = new byte[MaxBytes + 1];
        var length = 0;
        while (length < body.Length)
        {
            var count = await context.Request.Body.ReadAsync(body.AsMemory(length), context.RequestAborted);
            if (count == 0) break;
            length += count;
        }
        if (length > MaxBytes)
        {
            context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            return;
        }

        context.Request.Body = new MemoryStream(body, 0, length, writable: false);
        await next(context);
    }
}
