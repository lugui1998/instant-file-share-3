using InstantFileShare.Core;

namespace InstantFileShare.Agent;

internal static class ResponseHeaderWriter
{
    public static void ApplyFileResponseHeaders(HttpResponse response, string fileName, ShareFileResponseMetadata metadata)
    {
        response.ContentType = metadata.ContentType;
        response.Headers["Content-Disposition"] = BuildContentDispositionHeader(metadata.ContentDispositionType, fileName);
    }

    public static string BuildContentDispositionHeader(string dispositionType, string fileName)
    {
        var escapedFileName = fileName
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);

        return $"{dispositionType}; filename=\"{escapedFileName}\"; filename*=UTF-8''{Uri.EscapeDataString(fileName)}";
    }
}
