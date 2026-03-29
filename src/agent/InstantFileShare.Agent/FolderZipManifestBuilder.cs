namespace InstantFileShare.Agent;

internal static class FolderZipManifestBuilder
{
    public static IReadOnlyList<(string FullPath, string EntryPath)> Build(FolderSharePathResolver.ResolvedEntry rootEntry)
    {
        var manifest = new List<(string FullPath, string EntryPath)>();
        var pending = new Stack<FolderSharePathResolver.ResolvedEntry>();
        pending.Push(rootEntry);

        while (pending.Count > 0)
        {
            var current = pending.Pop();
            foreach (var child in FolderSharePathResolver.ListDirectory(current).Reverse())
            {
                if (!FolderSharePathResolver.TryResolveEntry(rootEntry.RootPath, child.RelativePath, out var resolvedChild) || resolvedChild is null)
                {
                    continue;
                }

                if (resolvedChild.IsDirectory)
                {
                    pending.Push(resolvedChild);
                    continue;
                }

                var entryPath = Path.GetRelativePath(rootEntry.FullPath, resolvedChild.FullPath).Replace('\\', '/');
                manifest.Add((resolvedChild.FullPath, entryPath));
            }
        }

        return manifest;
    }
}
