namespace InstantFileShare.Agent;

internal static class FolderSharePathResolver
{
    internal sealed record ResolvedEntry(
        string RootPath,
        string FullPath,
        string RelativePath,
        bool IsDirectory,
        string Name,
        long Size,
        DateTimeOffset LastModifiedAtUtc);

    internal sealed record DirectoryEntry(
        string Name,
        string RelativePath,
        bool IsDirectory,
        long Size,
        DateTimeOffset LastModifiedAtUtc);

    internal sealed record ZipEntry(
        string FullPath,
        string RelativePath,
        long Size,
        DateTimeOffset LastModifiedAtUtc);

    public static bool TryResolveEntry(string rootPath, string? relativePath, out ResolvedEntry? entry)
    {
        entry = null;

        if (!TryGetCanonicalRoot(rootPath, out var canonicalRoot))
        {
            return false;
        }

        var normalizedRelativePath = NormalizeRelativePath(relativePath);
        if (normalizedRelativePath is null)
        {
            return false;
        }

        var targetPath = string.IsNullOrEmpty(normalizedRelativePath)
            ? canonicalRoot
            : Path.GetFullPath(Path.Combine(canonicalRoot, normalizedRelativePath));
        if (!IsUnderRoot(canonicalRoot, targetPath))
        {
            return false;
        }

        if (!TryEnsureSafePath(canonicalRoot, targetPath, out var isDirectory))
        {
            return false;
        }

        var fileSystemInfo = isDirectory
            ? new DirectoryInfo(targetPath) as FileSystemInfo
            : new FileInfo(targetPath);
        entry = new ResolvedEntry(
            canonicalRoot,
            targetPath,
            string.IsNullOrEmpty(normalizedRelativePath) ? string.Empty : normalizedRelativePath.Replace('\\', '/'),
            isDirectory,
            fileSystemInfo.Name,
            fileSystemInfo is FileInfo fileInfo ? fileInfo.Length : 0,
            fileSystemInfo.LastWriteTimeUtc);
        return true;
    }

    public static IReadOnlyList<DirectoryEntry> ListDirectory(ResolvedEntry directory)
    {
        var directoryInfo = new DirectoryInfo(directory.FullPath);
        var entries = new List<DirectoryEntry>();

        foreach (var child in directoryInfo.EnumerateFileSystemInfos())
        {
            if (!child.Exists || HasReparsePoint(child.Attributes) || IsPartialDownloadFile(child))
            {
                continue;
            }

            var isDirectory = child.Attributes.HasFlag(FileAttributes.Directory);
            var relativePath = Path.GetRelativePath(directory.RootPath, child.FullName).Replace('\\', '/');
            entries.Add(new DirectoryEntry(
                child.Name,
                relativePath,
                isDirectory,
                child is FileInfo fileInfo ? fileInfo.Length : 0,
                child.LastWriteTimeUtc));
        }

        return entries
            .OrderByDescending((item) => item.IsDirectory)
            .ThenBy((item) => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static IReadOnlyList<ZipEntry> ListFilesForZip(string rootPath)
    {
        if (!TryResolveEntry(rootPath, null, out var rootEntry) || rootEntry is null || !rootEntry.IsDirectory)
        {
            return [];
        }

        var files = new List<ZipEntry>();
        PopulateZipEntries(rootEntry.RootPath, new DirectoryInfo(rootEntry.FullPath), files);
        return files
            .OrderBy((item) => item.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool TryGetCanonicalRoot(string rootPath, out string canonicalRoot)
    {
        canonicalRoot = string.Empty;
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            return false;
        }

        try
        {
            canonicalRoot = Path.GetFullPath(rootPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return false;
        }

        var rootDirectory = new DirectoryInfo(canonicalRoot);
        return rootDirectory.Exists && !HasReparsePoint(rootDirectory.Attributes);
    }

    private static string? NormalizeRelativePath(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return string.Empty;
        }

        var normalized = relativePath.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        normalized = normalized.TrimStart(Path.DirectorySeparatorChar);
        if (normalized.Length == 0 ||
            Path.IsPathRooted(normalized) ||
            normalized.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
        {
            return null;
        }

        var segments = normalized.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            return string.Empty;
        }

        foreach (var segment in segments)
        {
            if (segment is "." or "..")
            {
                return null;
            }

            if (OperatingSystem.IsWindows() &&
                (segment.EndsWith(' ') || segment.EndsWith('.')))
            {
                return null;
            }
        }

        return string.Join(Path.DirectorySeparatorChar, segments);
    }

    private static bool IsUnderRoot(string rootPath, string candidatePath)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (string.Equals(rootPath, candidatePath, comparison))
        {
            return true;
        }

        return candidatePath.StartsWith(rootPath + Path.DirectorySeparatorChar, comparison);
    }

    private static bool TryEnsureSafePath(string rootPath, string targetPath, out bool isDirectory)
    {
        isDirectory = true;
        var relativePath = Path.GetRelativePath(rootPath, targetPath);
        if (relativePath == ".")
        {
            var rootDirectory = new DirectoryInfo(rootPath);
            return rootDirectory.Exists && !HasReparsePoint(rootDirectory.Attributes);
        }

        var segments = relativePath.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        var currentPath = rootPath;

        for (var index = 0; index < segments.Length; index++)
        {
            currentPath = Path.Combine(currentPath, segments[index]);
            var isLastSegment = index == segments.Length - 1;

            if (Directory.Exists(currentPath))
            {
                var directoryInfo = new DirectoryInfo(currentPath);
                if (HasReparsePoint(directoryInfo.Attributes))
                {
                    return false;
                }

                isDirectory = true;
                continue;
            }

            if (File.Exists(currentPath))
            {
                var fileInfo = new FileInfo(currentPath);
                if (HasReparsePoint(fileInfo.Attributes))
                {
                    return false;
                }

                isDirectory = false;
                return isLastSegment;
            }

            return false;
        }

        return true;
    }

    private static void PopulateZipEntries(string rootPath, DirectoryInfo directoryInfo, ICollection<ZipEntry> files)
    {
        foreach (var child in directoryInfo.EnumerateFileSystemInfos())
        {
            if (!child.Exists || HasReparsePoint(child.Attributes) || IsPartialDownloadFile(child))
            {
                continue;
            }

            if (child is DirectoryInfo childDirectory)
            {
                PopulateZipEntries(rootPath, childDirectory, files);
                continue;
            }

            if (child is FileInfo fileInfo)
            {
                files.Add(new ZipEntry(
                    fileInfo.FullName,
                    Path.GetRelativePath(rootPath, fileInfo.FullName).Replace('\\', '/'),
                    fileInfo.Length,
                    fileInfo.LastWriteTimeUtc));
            }
        }
    }

    private static bool HasReparsePoint(FileAttributes attributes) => attributes.HasFlag(FileAttributes.ReparsePoint);

    private static bool IsPartialDownloadFile(FileSystemInfo entry)
    {
        return entry is FileInfo && entry.Name.EndsWith(".downloadpart", StringComparison.OrdinalIgnoreCase);
    }
}
