using System.Net;
using System.Text;
using System.Text.Json;

namespace InstantFileShare.Agent;

internal sealed class PublicShareHtmlRenderer(PublicShareAssetLocator assetLocator)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public bool TryRender(PublicSharePageModel pageModel, out string html, out string? error)
    {
        if (!assetLocator.TryGetShellAssets(out var shellAssets, out error))
        {
            html = string.Empty;
            return false;
        }

        var encodedTitle = WebUtility.HtmlEncode(pageModel.Title);
        var encodedDescription = WebUtility.HtmlEncode(pageModel.Description);
        var encodedUrl = WebUtility.HtmlEncode(pageModel.CanonicalUrl);
        var payload = JsonSerializer.Serialize(new { page = pageModel }, JsonOptions);

        var builder = new StringBuilder();
        builder.AppendLine("<!doctype html>");
        builder.AppendLine("<html lang=\"en\">");
        builder.AppendLine("  <head>");
        builder.AppendLine("    <meta charset=\"utf-8\" />");
        builder.AppendLine("    <meta name=\"viewport\" content=\"width=device-width, initial-scale=1\" />");
        builder.AppendLine($"    <title>{encodedTitle}</title>");
        builder.AppendLine($"    <meta name=\"description\" content=\"{encodedDescription}\" />");
        builder.AppendLine("    <meta name=\"robots\" content=\"noindex, nofollow\" />");
        builder.AppendLine("    <link rel=\"icon\" href=\"/favicon.ico\" />");
        builder.AppendLine($"    <meta property=\"og:title\" content=\"{encodedTitle}\" />");
        builder.AppendLine($"    <meta property=\"og:description\" content=\"{encodedDescription}\" />");
        builder.AppendLine("    <meta property=\"og:type\" content=\"website\" />");
        builder.AppendLine($"    <meta property=\"og:url\" content=\"{encodedUrl}\" />");
        builder.AppendLine($"    <meta property=\"og:site_name\" content=\"{WebUtility.HtmlEncode(pageModel.SiteName)}\" />");
        builder.AppendLine("    <meta name=\"twitter:card\" content=\"summary\" />");
        builder.AppendLine($"    <meta name=\"twitter:title\" content=\"{encodedTitle}\" />");
        builder.AppendLine($"    <meta name=\"twitter:description\" content=\"{encodedDescription}\" />");

        foreach (var stylesheetUrl in shellAssets.StylesheetUrls)
        {
            builder.AppendLine($"    <link rel=\"stylesheet\" href=\"{WebUtility.HtmlEncode(stylesheetUrl)}\" />");
        }

        builder.AppendLine("  </head>");
        builder.AppendLine("  <body>");
        builder.AppendLine("    <div id=\"public-share-app\"></div>");
        builder.AppendLine($"    <script>window.__IFS_PUBLIC_SHARE__ = {payload};</script>");
        builder.AppendLine($"    <script type=\"module\" src=\"{WebUtility.HtmlEncode(shellAssets.EntryScriptUrl)}\"></script>");
        builder.AppendLine("  </body>");
        builder.AppendLine("</html>");

        html = builder.ToString();
        return true;
    }
}
