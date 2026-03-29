namespace InstantFileShare.Agent;

internal static class ControlPortRestrictionExtensions
{
    public static IApplicationBuilder UseControlPortRestriction(this IApplicationBuilder app, int localApiPort)
    {
        return app.Use(async (context, next) =>
        {
            var path = context.Request.Path;
            var isControlPath = path.StartsWithSegments("/api") || path.StartsWithSegments("/ws");
            if (isControlPath && context.Connection.LocalPort != localApiPort)
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            await next();
        });
    }
}
