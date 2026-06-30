namespace InstantFileShare.Agent;

internal static class FolderZipManifestBuilder
{
    public sealed record Entry(string FullPath, string EntryPath, long SizeBytes);

    public static IReadOnlyList<Entry> Build(FolderSharePathResolver.ResolvedEntry rootEntry)
    {
        var manifest = new List<Entry>();
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

                var fileInfo = new FileInfo(resolvedChild.FullPath);
                var entryPath = Path.GetRelativePath(rootEntry.FullPath, resolvedChild.FullPath).Replace('\\', '/');
                manifest.Add(new Entry(resolvedChild.FullPath, entryPath, fileInfo.Length));
            }
        }

        return manifest;
    }
}
