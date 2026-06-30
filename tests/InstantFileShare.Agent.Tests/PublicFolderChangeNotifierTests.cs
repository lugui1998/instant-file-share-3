using InstantFileShare.Agent;

namespace InstantFileShare.Agent.Tests;

public sealed class PublicFolderChangeNotifierTests
{
    [Fact]
    public async Task ListenAsync_PublishesDebouncedFolderChangeNotification()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), "ifs-folder-change-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(rootPath);

        try
        {
            using var notifier = new PublicFolderChangeNotifier();
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await using var listener = notifier.ListenAsync(rootPath, "docs", cancellation.Token).GetAsyncEnumerator(cancellation.Token);
            var moveNextTask = listener.MoveNextAsync().AsTask();

            await File.WriteAllTextAsync(Path.Combine(rootPath, "new-file.txt"), "content", cancellation.Token);

            Assert.True(await moveNextTask);
            Assert.Equal("folder-changed", listener.Current.Type);
            Assert.Equal("docs", listener.Current.RelativePath);
            Assert.Equal(1, listener.Current.Version);
            Assert.True(listener.Current.OccurredAtUtc <= DateTimeOffset.UtcNow);
        }
        finally
        {
            DeleteDirectoryEventually(rootPath);
        }
    }

    private static void DeleteDirectoryEventually(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        for (var attempt = 1; attempt <= 10; attempt++)
        {
            try
            {
                Directory.Delete(path, recursive: true);
                return;
            }
            catch (IOException) when (attempt < 10)
            {
                Thread.Sleep(100);
            }
            catch (UnauthorizedAccessException) when (attempt < 10)
            {
                Thread.Sleep(100);
            }
        }
    }
}
