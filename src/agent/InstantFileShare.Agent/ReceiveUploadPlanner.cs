using Microsoft.AspNetCore.Http;

namespace InstantFileShare.Agent;

internal sealed record ReceiveUploadCandidate(
    IFormFile File,
    string ClientRelativePath);

internal sealed record PlannedReceiveUpload(
    IFormFile File,
    string ClientRelativePath,
    string StoredRelativePath,
    string DestinationPath);

internal sealed record ReceiveUploadFileResult(
    string FileName,
    string RelativePath,
    string? StoredRelativePath,
    bool Success,
    string? Message,
    long SizeBytes);

internal sealed record ReceiveUploadBatchResult(
    int UploadedCount,
    int FailedCount,
    long RemainingQuotaBytes,
    IReadOnlyList<ReceiveUploadFileResult> Results);

internal static class ReceiveUploadPlanner
{
    public static (IReadOnlyList<PlannedReceiveUpload> Planned, IReadOnlyList<ReceiveUploadFileResult> Rejected) Plan(
        string targetDirectoryPath,
        IReadOnlyList<ReceiveUploadCandidate> candidates)
    {
        var planned = new List<PlannedReceiveUpload>();
        var rejected = new List<ReceiveUploadFileResult>();
        var rootAliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var reservedDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            NormalizePath(targetDirectoryPath),
        };
        var reservedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var candidate in candidates)
        {
            var rawRelativePath = string.IsNullOrWhiteSpace(candidate.ClientRelativePath)
                ? Path.GetFileName(candidate.File.FileName)
                : candidate.ClientRelativePath;

            if (!ReceiveUploadPathResolver.TryNormalizeRelativePath(rawRelativePath, out var normalizedRelativePath, out var error))
            {
                rejected.Add(new ReceiveUploadFileResult(candidate.File.FileName, rawRelativePath, null, false, error, candidate.File.Length));
                continue;
            }

            var segments = normalizedRelativePath.Split('/');
            if (segments.Length > 1)
            {
                var originalRoot = segments[0];
                if (!rootAliases.TryGetValue(originalRoot, out var aliasedRoot))
                {
                    if (!ReceiveUploadPathResolver.TryResolveUnderRoot(targetDirectoryPath, originalRoot, out var rootPath, out error))
                    {
                        rejected.Add(new ReceiveUploadFileResult(candidate.File.FileName, normalizedRelativePath, null, false, error, candidate.File.Length));
                        continue;
                    }

                    rootPath = EnsureUniqueDirectoryPath(rootPath, reservedDirectories, reservedFiles);
                    aliasedRoot = Path.GetFileName(rootPath);
                    rootAliases[originalRoot] = aliasedRoot;
                    reservedDirectories.Add(NormalizePath(rootPath));
                }

                segments[0] = aliasedRoot;
            }

            var aliasedRelativePath = string.Join('/', segments);
            if (!ReceiveUploadPathResolver.TryResolveUnderRoot(targetDirectoryPath, aliasedRelativePath, out var resolvedPath, out error))
            {
                rejected.Add(new ReceiveUploadFileResult(candidate.File.FileName, normalizedRelativePath, null, false, error, candidate.File.Length));
                continue;
            }

            if (!TryReserveAncestors(targetDirectoryPath, resolvedPath, reservedDirectories, reservedFiles, out error))
            {
                rejected.Add(new ReceiveUploadFileResult(candidate.File.FileName, normalizedRelativePath, null, false, error, candidate.File.Length));
                continue;
            }

            resolvedPath = EnsureUniqueFilePath(resolvedPath, reservedDirectories, reservedFiles);
            reservedFiles.Add(NormalizePath(resolvedPath));

            planned.Add(new PlannedReceiveUpload(
                candidate.File,
                normalizedRelativePath,
                Path.GetRelativePath(targetDirectoryPath, resolvedPath).Replace('\\', '/'),
                resolvedPath));
        }

        return (planned, rejected);
    }

    private static bool TryReserveAncestors(
        string targetDirectoryPath,
        string resolvedFilePath,
        ISet<string> reservedDirectories,
        ISet<string> reservedFiles,
        out string? error)
    {
        error = null;

        var fullTargetDirectoryPath = NormalizePath(targetDirectoryPath);
        var relativeParentPath = Path.GetRelativePath(fullTargetDirectoryPath, Path.GetDirectoryName(resolvedFilePath) ?? fullTargetDirectoryPath);
        if (relativeParentPath is "." or "")
        {
            return true;
        }

        var currentPath = fullTargetDirectoryPath;
        foreach (var segment in relativeParentPath.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            currentPath = Path.Combine(currentPath, segment);
            var normalizedCurrentPath = NormalizePath(currentPath);

            if (Directory.Exists(currentPath) &&
                (File.GetAttributes(currentPath) & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint)
            {
                error = "The uploaded path traverses a linked folder, which is not allowed.";
                return false;
            }

            if (File.Exists(currentPath) || reservedFiles.Contains(normalizedCurrentPath))
            {
                error = "A file already exists where an uploaded folder needs to be created.";
                return false;
            }

            reservedDirectories.Add(normalizedCurrentPath);
        }

        return true;
    }

    private static string EnsureUniqueDirectoryPath(string directoryPath, ISet<string> reservedDirectories, ISet<string> reservedFiles)
    {
        var candidate = NormalizePath(directoryPath);
        if (!reservedDirectories.Contains(candidate) &&
            !reservedFiles.Contains(candidate) &&
            !Directory.Exists(candidate) &&
            !File.Exists(candidate))
        {
            return candidate;
        }

        var parentPath = Path.GetDirectoryName(candidate) ?? throw new InvalidOperationException("Directory path has no parent.");
        var baseName = Path.GetFileName(candidate);

        for (var index = 1; index < 10_000; index++)
        {
            var renamedCandidate = NormalizePath(Path.Combine(parentPath, $"{baseName} ({index})"));
            if (!reservedDirectories.Contains(renamedCandidate) &&
                !reservedFiles.Contains(renamedCandidate) &&
                !Directory.Exists(renamedCandidate) &&
                !File.Exists(renamedCandidate))
            {
                return renamedCandidate;
            }
        }

        throw new InvalidOperationException("Failed to allocate a unique destination directory.");
    }

    private static string EnsureUniqueFilePath(string filePath, ISet<string> reservedDirectories, ISet<string> reservedFiles)
    {
        var candidate = NormalizePath(filePath);
        if (!reservedDirectories.Contains(candidate) &&
            !reservedFiles.Contains(candidate) &&
            !File.Exists(candidate) &&
            !Directory.Exists(candidate))
        {
            return candidate;
        }

        var parentPath = Path.GetDirectoryName(candidate) ?? throw new InvalidOperationException("File path has no parent.");
        var fileName = Path.GetFileNameWithoutExtension(candidate);
        var extension = Path.GetExtension(candidate);

        for (var index = 1; index < 10_000; index++)
        {
            var renamedCandidate = NormalizePath(Path.Combine(parentPath, $"{fileName} ({index}){extension}"));
            if (!reservedDirectories.Contains(renamedCandidate) &&
                !reservedFiles.Contains(renamedCandidate) &&
                !File.Exists(renamedCandidate) &&
                !Directory.Exists(renamedCandidate))
            {
                return renamedCandidate;
            }
        }

        throw new InvalidOperationException("Failed to allocate a unique destination file.");
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
