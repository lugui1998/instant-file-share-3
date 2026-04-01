using System.Text;

namespace InstantFileShare.Agent;

internal static class ReceiveUploadPathResolver
{
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    public static bool TryNormalizeRelativePath(string rawPath, out string normalizedPath, out string? error)
    {
        normalizedPath = string.Empty;
        error = null;

        if (string.IsNullOrWhiteSpace(rawPath))
        {
            error = "The uploaded path is empty.";
            return false;
        }

        var candidate = rawPath.Replace('\\', '/').Trim();
        if (candidate.IndexOf('\0') >= 0 || Path.IsPathRooted(candidate) || candidate.StartsWith("//", StringComparison.Ordinal))
        {
            error = "The uploaded path is invalid.";
            return false;
        }

        var segments = candidate.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length == 0)
        {
            error = "The uploaded path is invalid.";
            return false;
        }

        var builder = new StringBuilder();
        foreach (var segment in segments)
        {
            if (!IsValidSegment(segment, out error))
            {
                normalizedPath = string.Empty;
                return false;
            }

            if (builder.Length > 0)
            {
                builder.Append('/');
            }

            builder.Append(segment);
        }

        normalizedPath = builder.ToString();
        return true;
    }

    public static bool TryResolveUnderRoot(string rootPath, string relativePath, out string resolvedPath, out string? error)
    {
        resolvedPath = string.Empty;
        error = null;

        var fullRootPath = NormalizePath(rootPath);
        var candidatePath = Path.GetFullPath(Path.Combine(fullRootPath, relativePath.Replace('/', Path.DirectorySeparatorChar)));

        if (!candidatePath.StartsWith(fullRootPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(candidatePath, fullRootPath, StringComparison.OrdinalIgnoreCase))
        {
            error = "The uploaded path escapes the receive folder.";
            return false;
        }

        resolvedPath = candidatePath;
        return true;
    }

    public static string EnsureUniqueDirectoryPath(string directoryPath)
    {
        if (!Directory.Exists(directoryPath) && !File.Exists(directoryPath))
        {
            return directoryPath;
        }

        var parentPath = Path.GetDirectoryName(directoryPath) ?? throw new InvalidOperationException("Directory path has no parent.");
        var baseName = Path.GetFileName(directoryPath);

        for (var index = 1; index < 10_000; index++)
        {
            var candidate = Path.Combine(parentPath, $"{baseName} ({index})");
            if (!Directory.Exists(candidate) && !File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException("Failed to allocate a unique destination directory.");
    }

    public static string EnsureUniqueFilePath(string filePath)
    {
        if (!File.Exists(filePath) && !Directory.Exists(filePath))
        {
            return filePath;
        }

        var parentPath = Path.GetDirectoryName(filePath) ?? throw new InvalidOperationException("File path has no parent.");
        var fileName = Path.GetFileNameWithoutExtension(filePath);
        var extension = Path.GetExtension(filePath);

        for (var index = 1; index < 10_000; index++)
        {
            var candidate = Path.Combine(parentPath, $"{fileName} ({index}){extension}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException("Failed to allocate a unique destination file.");
    }

    private static bool IsValidSegment(string segment, out string? error)
    {
        error = null;

        if (string.IsNullOrWhiteSpace(segment) || segment is "." or ".." || segment.EndsWith(' ') || segment.EndsWith('.'))
        {
            error = "The uploaded path contains an invalid segment.";
            return false;
        }

        if (segment.Contains(':') || segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            error = "The uploaded path contains invalid characters.";
            return false;
        }

        var stem = Path.GetFileNameWithoutExtension(segment);
        if (ReservedNames.Contains(stem))
        {
            error = "The uploaded path uses a reserved Windows name.";
            return false;
        }

        return true;
    }

    private static string NormalizePath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var rootPath = Path.GetPathRoot(fullPath);
        return string.Equals(fullPath, rootPath, StringComparison.OrdinalIgnoreCase)
            ? fullPath
            : fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
}
