namespace InstantFileShare.Agent;

internal static class MetadataCrawlerDetector
{
    public static string? ResolveMetadataCrawlerName(HttpRequest request)
    {
        if (request.Headers.ContainsKey("Range"))
        {
            return null;
        }

        var userAgent = request.Headers.UserAgent.ToString();
        if (string.IsNullOrWhiteSpace(userAgent))
        {
            return null;
        }

        var normalizedUserAgent = userAgent.ToLowerInvariant();
        if (normalizedUserAgent.Contains("discordbot", StringComparison.Ordinal))
        {
            return "Discordbot";
        }

        if (normalizedUserAgent.Contains("whatsapp", StringComparison.Ordinal))
        {
            return "WhatsApp";
        }

        if (normalizedUserAgent.Contains("facebookexternalhit", StringComparison.Ordinal))
        {
            return "Facebook";
        }

        if (normalizedUserAgent.Contains("twitterbot", StringComparison.Ordinal))
        {
            return "Twitterbot";
        }

        if (normalizedUserAgent.Contains("slackbot", StringComparison.Ordinal))
        {
            return "Slackbot";
        }

        if (normalizedUserAgent.Contains("linkedinbot", StringComparison.Ordinal))
        {
            return "LinkedIn";
        }

        if (normalizedUserAgent.Contains("telegrambot", StringComparison.Ordinal))
        {
            return "Telegram";
        }

        if (normalizedUserAgent.Contains("skypeuripreview", StringComparison.Ordinal))
        {
            return "Skype Preview";
        }

        if (normalizedUserAgent.Contains("googlebot", StringComparison.Ordinal))
        {
            return "Googlebot";
        }

        if (normalizedUserAgent.Contains("preview", StringComparison.Ordinal))
        {
            return "Link Preview";
        }

        return null;
    }
}
