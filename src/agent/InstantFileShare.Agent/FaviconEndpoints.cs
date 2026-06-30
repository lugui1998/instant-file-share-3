namespace InstantFileShare.Agent;

internal static class FaviconEndpoints
{
    private const string FaviconContentType = "image/x-icon";
    private const string FaviconFileName = "icon.ico";

    public static IEndpointRouteBuilder MapFaviconEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/favicon.ico", IResult (AgentApplicationOptions applicationOptions) =>
        {
            return TryResolveFaviconPath(applicationOptions, out var faviconPath)
                ? Results.File(faviconPath, FaviconContentType)
                : Results.NotFound();
        });

        return endpoints;
    }

    internal static bool TryResolveFaviconPath(AgentApplicationOptions applicationOptions, out string faviconPath)
    {
        foreach (var candidatePath in GetCandidatePaths(applicationOptions))
        {
            if (File.Exists(candidatePath))
            {
                faviconPath = candidatePath;
                return true;
            }
        }

        faviconPath = string.Empty;
        return false;
    }

    private static IEnumerable<string> GetCandidatePaths(AgentApplicationOptions applicationOptions)
    {
        yield return Path.Combine(AppContext.BaseDirectory, FaviconFileName);
        yield return Path.Combine(applicationOptions.RepositoryRoot, "src", "agent", "InstantFileShare.Agent", FaviconFileName);
        yield return Path.Combine(applicationOptions.RepositoryRoot, "src", "ui", FaviconFileName);
    }
}
