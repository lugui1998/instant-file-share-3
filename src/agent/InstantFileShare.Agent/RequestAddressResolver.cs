using System.Net;
using System.Security.Cryptography;
using Microsoft.Extensions.Primitives;

namespace InstantFileShare.Agent;

internal static class RequestAddressResolver
{
    public static string? ResolveClientIpAddress(HttpContext context)
    {
        var remoteIpAddress = context.Connection.RemoteIpAddress;
        if (remoteIpAddress is not null && !IPAddress.IsLoopback(remoteIpAddress))
        {
            return remoteIpAddress.ToString();
        }

        return TryResolveForwardedClientIp(context.Request.Headers) ?? remoteIpAddress?.ToString();
    }

    public static string? BuildClientFingerprint(string? remoteAddress, string? userAgent)
    {
        if (string.IsNullOrWhiteSpace(remoteAddress) || string.IsNullOrWhiteSpace(userAgent))
        {
            return null;
        }

        var payload = $"{remoteAddress}\n{userAgent.Trim()}";
        return Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
    }

    public static string? TryResolveForwardedClientIp(IHeaderDictionary headers)
    {
        if (TryResolveHeaderIp(headers, "CF-Connecting-IP", out var cloudflareIp))
        {
            return cloudflareIp;
        }

        if (TryResolveHeaderIp(headers, "True-Client-IP", out var trueClientIp))
        {
            return trueClientIp;
        }

        if (headers.TryGetValue("X-Forwarded-For", out var forwardedForValues))
        {
            foreach (var forwardedForValue in forwardedForValues)
            {
                if (string.IsNullOrWhiteSpace(forwardedForValue))
                {
                    continue;
                }

                var segments = forwardedForValue.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                foreach (var segment in segments)
                {
                    if (TryNormalizeIpAddress(segment, out var forwardedIp))
                    {
                        return forwardedIp;
                    }
                }
            }
        }

        if (headers.TryGetValue("Forwarded", out var forwardedValues))
        {
            foreach (var forwardedValue in forwardedValues)
            {
                if (string.IsNullOrWhiteSpace(forwardedValue))
                {
                    continue;
                }

                var entries = forwardedValue.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                foreach (var entry in entries)
                {
                    var segments = entry.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    foreach (var segment in segments)
                    {
                        if (!segment.StartsWith("for=", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        var candidate = segment[4..].Trim().Trim('"');
                        if (TryNormalizeIpAddress(candidate, out var forwardedIp))
                        {
                            return forwardedIp;
                        }
                    }
                }
            }
        }

        return null;
    }

    private static bool TryResolveHeaderIp(IHeaderDictionary headers, string headerName, out string? ipAddress)
    {
        ipAddress = null;
        if (!headers.TryGetValue(headerName, out StringValues headerValues))
        {
            return false;
        }

        foreach (var headerValue in headerValues)
        {
            if (TryNormalizeIpAddress(headerValue, out ipAddress))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryNormalizeIpAddress(string? rawValue, out string? ipAddress)
    {
        ipAddress = null;
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return false;
        }

        var candidate = rawValue.Trim().Trim('"');
        if (candidate.Length == 0 || candidate.Equals("unknown", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (candidate[0] == '[')
        {
            var closingBracketIndex = candidate.IndexOf(']');
            if (closingBracketIndex <= 1)
            {
                return false;
            }

            candidate = candidate[1..closingBracketIndex];
        }
        else
        {
            var colonIndex = candidate.LastIndexOf(':');
            if (colonIndex > 0 && candidate.IndexOf(':') == colonIndex)
            {
                candidate = candidate[..colonIndex];
            }
        }

        if (!IPAddress.TryParse(candidate, out var parsedIpAddress))
        {
            return false;
        }

        ipAddress = parsedIpAddress.ToString();
        return true;
    }
}
