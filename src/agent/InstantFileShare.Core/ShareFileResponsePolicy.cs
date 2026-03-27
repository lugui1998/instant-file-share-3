namespace InstantFileShare.Core;

public enum ShareFileTypeCategory
{
    Other = 0,
    Image = 1,
    Video = 2,
    Pdf = 3,
}

public sealed record ShareFileResponseMetadata(string ContentType, ShareFileTypeCategory Category, bool PreferInline)
{
    public string ContentDispositionType => PreferInline ? "inline" : "attachment";
}

public static class ShareFileResponsePolicy
{
    public const string DefaultContentType = "application/octet-stream";

    private static readonly IReadOnlyDictionary<string, (string ContentType, ShareFileTypeCategory Category)> KnownContentTypes = new Dictionary<string, (string ContentType, ShareFileTypeCategory Category)>(StringComparer.OrdinalIgnoreCase)
    {
        [".avif"] = ("image/avif", ShareFileTypeCategory.Image),
        [".bmp"] = ("image/bmp", ShareFileTypeCategory.Image),
        [".gif"] = ("image/gif", ShareFileTypeCategory.Image),
        [".heic"] = ("image/heic", ShareFileTypeCategory.Image),
        [".heif"] = ("image/heif", ShareFileTypeCategory.Image),
        [".jpeg"] = ("image/jpeg", ShareFileTypeCategory.Image),
        [".jfif"] = ("image/jpeg", ShareFileTypeCategory.Image),
        [".jpg"] = ("image/jpeg", ShareFileTypeCategory.Image),
        [".m4v"] = ("video/mp4", ShareFileTypeCategory.Video),
        [".mov"] = ("video/quicktime", ShareFileTypeCategory.Video),
        [".mp4"] = ("video/mp4", ShareFileTypeCategory.Video),
        [".ogv"] = ("video/ogg", ShareFileTypeCategory.Video),
        [".pdf"] = ("application/pdf", ShareFileTypeCategory.Pdf),
        [".png"] = ("image/png", ShareFileTypeCategory.Image),
        [".webm"] = ("video/webm", ShareFileTypeCategory.Video),
        [".webp"] = ("image/webp", ShareFileTypeCategory.Image),
    };

    public static ShareFileResponseMetadata Resolve(string? fileName, AppSettings? settings = null)
    {
        var extension = Path.GetExtension(fileName);
        if (!string.IsNullOrWhiteSpace(extension) && KnownContentTypes.TryGetValue(extension, out var metadata))
        {
            return new ShareFileResponseMetadata(metadata.ContentType, metadata.Category, ShouldOpenInBrowser(metadata.Category, settings));
        }

        return new ShareFileResponseMetadata(DefaultContentType, ShareFileTypeCategory.Other, PreferInline: false);
    }

    private static bool ShouldOpenInBrowser(ShareFileTypeCategory category, AppSettings? settings)
    {
        if (settings is null)
        {
            return true;
        }

        return category switch
        {
            ShareFileTypeCategory.Image => settings.OpenImagesInBrowser,
            ShareFileTypeCategory.Video => settings.OpenVideosInBrowser,
            ShareFileTypeCategory.Pdf => settings.OpenPdfInBrowser,
            _ => false,
        };
    }
}
