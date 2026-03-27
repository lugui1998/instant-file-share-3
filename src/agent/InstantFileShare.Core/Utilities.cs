using System.Security.Cryptography;
using System.Text;

namespace InstantFileShare.Core;

public static class ShareTokenGenerator
{
    private const string Alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";
    public const int MinLength = 6;
    public const int RecommendedLength = 11;
    public const int MaxLength = 128;

    public static string Generate(int length = RecommendedLength)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(length, MinLength);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(length, MaxLength);

        var buffer = new char[length];
        for (var index = 0; index < buffer.Length; index++)
        {
            buffer[index] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        }

        return new string(buffer);
    }
}

public static class FileNameSlug
{
    public static string? Create(string fileName, int maxLength = 48)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return null;
        }

        var name = Path.GetFileNameWithoutExtension(fileName).Trim().ToLowerInvariant();
        var builder = new StringBuilder();

        foreach (var character in name)
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(character);
                continue;
            }

            if (builder.Length > 0 && builder[^1] != '-')
            {
                builder.Append('-');
            }
        }

        var slug = builder.ToString().Trim('-');
        if (slug.Length == 0)
        {
            return null;
        }

        if (slug.Length > maxLength)
        {
            slug = slug[..maxLength].Trim('-');
        }

        return slug.Length == 0 ? null : slug;
    }
}

public static class ShareUrlBuilder
{
    public static string Build(string baseUrl, string token, string? slug, string? fileName = null)
    {
        baseUrl = baseUrl.TrimEnd('/');
        var friendlySegment = BuildFriendlySegment(slug, fileName);

        return string.IsNullOrWhiteSpace(friendlySegment)
            ? $"{baseUrl}/s/{token}"
            : $"{baseUrl}/s/{token}/{Uri.EscapeDataString(friendlySegment)}";
    }

    public static string? BuildFriendlySegment(string? slug, string? fileName)
    {
        if (string.IsNullOrWhiteSpace(slug))
        {
            return null;
        }

        var extension = Path.GetExtension(fileName);
        if (string.IsNullOrWhiteSpace(extension) || slug.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
        {
            return slug;
        }

        return $"{slug}{extension}";
    }
}
