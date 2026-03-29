using System.Text.Json;

namespace InstantFileShare.Agent;

internal sealed class PublicShareAssetLocator(AgentApplicationOptions applicationOptions)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly object _sync = new();
    private PublicShareShellAssets? _cachedShellAssets;

    public bool TryGetShellAssets(out PublicShareShellAssets shellAssets, out string? error)
    {
        lock (_sync)
        {
            if (_cachedShellAssets is not null)
            {
                shellAssets = _cachedShellAssets;
                error = null;
                return true;
            }

            if (TryLoadShellAssets(out shellAssets, out error))
            {
                _cachedShellAssets = shellAssets;
                return true;
            }

            return false;
        }
    }

    public bool TryResolveAsset(string assetPath, out string resolvedPath)
    {
        foreach (var rootPath in GetCandidateRoots())
        {
            if (!Directory.Exists(rootPath))
            {
                continue;
            }

            if (TryResolveAssetUnderRoot(rootPath, assetPath, out resolvedPath))
            {
                return true;
            }
        }

        resolvedPath = string.Empty;
        return false;
    }

    private bool TryLoadShellAssets(out PublicShareShellAssets shellAssets, out string? error)
    {
        foreach (var rootPath in GetCandidateRoots())
        {
            if (!Directory.Exists(rootPath))
            {
                continue;
            }

            var manifestPath = ResolveManifestPath(rootPath);
            if (manifestPath is null)
            {
                continue;
            }

            var manifest = JsonSerializer.Deserialize<Dictionary<string, ViteManifestEntry>>(File.ReadAllText(manifestPath), JsonOptions);
            var entry = manifest?
                .Values
                .FirstOrDefault(candidate => candidate.IsEntry && !string.IsNullOrWhiteSpace(candidate.File));

            if (entry?.File is null)
            {
                continue;
            }

            shellAssets = new PublicShareShellAssets(
                EntryScriptUrl: BuildAssetUrl(entry.File),
                StylesheetUrls: (entry.Css ?? []).Select(BuildAssetUrl).ToArray());
            error = null;
            return true;
        }

        shellAssets = default!;
        error = "Public share UI assets were not found. Build the public-share bundle and make sure the dist output is available to the agent.";
        return false;
    }

    private static string? ResolveManifestPath(string rootPath)
    {
        var candidates = new[]
        {
            Path.Combine(rootPath, "manifest.json"),
            Path.Combine(rootPath, ".vite", "manifest.json"),
        };

        return candidates.FirstOrDefault(File.Exists);
    }

    private static bool TryResolveAssetUnderRoot(string rootPath, string assetPath, out string resolvedPath)
    {
        resolvedPath = string.Empty;

        var normalizedPath = assetPath.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar);
        if (string.IsNullOrWhiteSpace(normalizedPath))
        {
            return false;
        }

        var fullRootPath = Path.GetFullPath(rootPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var candidatePath = Path.GetFullPath(Path.Combine(fullRootPath, normalizedPath));
        if (!candidatePath.StartsWith(fullRootPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(candidatePath, fullRootPath, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!File.Exists(candidatePath))
        {
            return false;
        }

        resolvedPath = candidatePath;
        return true;
    }

    private IEnumerable<string> GetCandidateRoots()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in new[]
                 {
                     applicationOptions.PublicShareAssetsDirectory,
                     Path.Combine(AppContext.BaseDirectory, "public-share"),
                     Path.Combine(applicationOptions.RepositoryRoot, "src", "ui", "dist-public-share"),
                 })
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            var fullPath = Path.GetFullPath(candidate);
            if (seen.Add(fullPath))
            {
                yield return fullPath;
            }
        }
    }

    private static string BuildAssetUrl(string relativePath)
    {
        return $"/public-share-assets/{relativePath.Replace('\\', '/')}";
    }

    private sealed record ViteManifestEntry(
        string? File,
        bool IsEntry,
        IReadOnlyList<string>? Css);
}

internal sealed record PublicShareShellAssets(
    string EntryScriptUrl,
    IReadOnlyList<string> StylesheetUrls);
