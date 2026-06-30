using System.Net;
using System.Security.Cryptography;
using Microsoft.Extensions.Primitives;

namespace InstantFileShare.Agent;

internal static class RequestAddressResolver
{
    public static string? ResolveClientIpAddress(HttpContext context)
    {
        var remoteIpAddress = context.Connection.RemoteIpAddress;
        if (ShouldTrustForwardedHeaders(remoteIpAddress))
        {
            return TryResolveForwardedClientIp(context.Request.Headers) ?? remoteIpAddress?.ToString();
        }

        if (remoteIpAddress is not null)
        {
            return remoteIpAddress.ToString();
        }

        return TryResolveForwardedClientIp(context.Request.Headers);
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

        if (TryResolveHeaderIp(headers, "X-Real-IP", out var realIp))
        {
            return realIp;
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

    private static bool ShouldTrustForwardedHeaders(IPAddress? remoteIpAddress)
    {
        return remoteIpAddress is null || IsPrivateOrLocalAddress(remoteIpAddress);
    }

    private static bool IsPrivateOrLocalAddress(IPAddress ipAddress)
    {
        if (ipAddress.IsIPv4MappedToIPv6)
        {
            ipAddress = ipAddress.MapToIPv4();
        }

        if (IPAddress.IsLoopback(ipAddress))
        {
            return true;
        }

        var bytes = ipAddress.GetAddressBytes();
        return ipAddress.AddressFamily switch
        {
            System.Net.Sockets.AddressFamily.InterNetwork => IsPrivateOrLocalIpv4(bytes),
            System.Net.Sockets.AddressFamily.InterNetworkV6 => IsPrivateOrLocalIpv6(bytes),
            _ => false,
        };
    }

    private static bool IsPrivateOrLocalIpv4(byte[] bytes)
    {
        return bytes[0] == 10
            || bytes[0] == 127
            || (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
            || (bytes[0] == 192 && bytes[1] == 168)
            || (bytes[0] == 169 && bytes[1] == 254);
    }

    private static bool IsPrivateOrLocalIpv6(byte[] bytes)
    {
        return (bytes[0] & 0xFE) == 0xFC
            || (bytes[0] == 0xFE && (bytes[1] & 0xC0) == 0x80);
    }
}
