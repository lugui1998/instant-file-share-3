using System.Security.Cryptography;

namespace InstantFileShare.Agent;

internal static class DownloadSessionManager
{
    public static (string SessionId, bool SetCookie) ResolveDownloadSession(HttpContext context, string token)
    {
        var cookieName = GetCookieName(token);
        if (context.Request.Cookies.TryGetValue(cookieName, out var existingSessionId) &&
            !string.IsNullOrWhiteSpace(existingSessionId))
        {
            return (existingSessionId, false);
        }

        return (Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant(), true);
    }

    public static void AppendCookie(HttpResponse response, HttpRequest request, string token, string sessionId)
    {
        response.Cookies.Append(
            GetCookieName(token),
            sessionId,
            new CookieOptions
            {
                HttpOnly = true,
                IsEssential = true,
                SameSite = SameSiteMode.Lax,
                Path = $"/s/{token}",
                Secure = request.IsHttps,
                Expires = DateTimeOffset.UtcNow.AddDays(30),
            });
    }

    public static string GetCookieName(string token) => $"ifs-download-{token}";
}
