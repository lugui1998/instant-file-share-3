using InstantFileShare.Core;
using System.Text;

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
        var escapedFileName = BuildAsciiFileNameFallback(fileName)
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);

        return $"{dispositionType}; filename=\"{escapedFileName}\"; filename*=UTF-8''{Uri.EscapeDataString(fileName)}";
    }

    private static string BuildAsciiFileNameFallback(string fileName)
    {
        var builder = new StringBuilder(fileName.Length);
        foreach (var ch in fileName)
        {
            builder.Append(ch is >= ' ' and <= '~' ? ch : '_');
        }

        return builder.Length == 0 ? "download" : builder.ToString();
    }
}
