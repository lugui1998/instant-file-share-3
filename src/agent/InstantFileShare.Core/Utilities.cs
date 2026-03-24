using System.Security.Cryptography;
using System.Text;

namespace InstantFileShare.Core;

public static class ShareTokenGenerator
{
    private const string Alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";

    public static string Generate(int byteCount = 16)
    {
        Span<byte> buffer = stackalloc byte[byteCount];
        RandomNumberGenerator.Fill(buffer);
        return ToBase62(buffer);
    }

    private static string ToBase62(ReadOnlySpan<byte> bytes)
    {
        var value = new System.Numerics.BigInteger(bytes, isUnsigned: true, isBigEndian: true);
        if (value.IsZero)
        {
            return Alphabet[0].ToString();
        }

        var builder = new StringBuilder();
        var target = new System.Numerics.BigInteger(62);

        while (value > 0)
        {
            value = System.Numerics.BigInteger.DivRem(value, target, out var remainder);
            builder.Insert(0, Alphabet[(int)remainder]);
        }

        while (builder.Length < 22)
        {
            builder.Insert(0, Alphabet[0]);
        }

        return builder.ToString();
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
    public static string Build(string baseUrl, string token, string? slug)
    {
        baseUrl = baseUrl.TrimEnd('/');
        return string.IsNullOrWhiteSpace(slug)
            ? $"{baseUrl}/s/{token}"
            : $"{baseUrl}/s/{token}/{Uri.EscapeDataString(slug)}";
    }
}
