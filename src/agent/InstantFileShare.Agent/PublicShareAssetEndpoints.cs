using Microsoft.AspNetCore.StaticFiles;

namespace InstantFileShare.Agent;

internal static class PublicShareAssetEndpoints
{
    private static readonly FileExtensionContentTypeProvider ContentTypeProvider = new();

    public static IEndpointRouteBuilder MapPublicShareAssetEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/public-share-assets/{**assetPath}", IResult (string assetPath, PublicShareAssetLocator assetLocator) =>
        {
            if (string.IsNullOrWhiteSpace(assetPath) || !assetLocator.TryResolveAsset(assetPath, out var resolvedPath))
            {
                return Results.NotFound();
            }

            if (!ContentTypeProvider.TryGetContentType(resolvedPath, out var contentType))
            {
                contentType = "application/octet-stream";
            }

            return Results.File(resolvedPath, contentType);
        });

        return endpoints;
    }
}
