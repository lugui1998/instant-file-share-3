using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace InstantFileShare.Agent;

internal sealed class PublicFolderChangeNotifier : IDisposable
{
    private static readonly TimeSpan ChangeDebounceDelay = TimeSpan.FromMilliseconds(500);
    private readonly object _gate = new();
    private readonly Dictionary<string, WatchedFolder> _watchedFolders = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    public async IAsyncEnumerable<PublicFolderChangeEvent> ListenAsync(
        string directoryPath,
        string relativePath,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        WatchedFolder.FolderSubscription subscription;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (!_watchedFolders.TryGetValue(directoryPath, out var watchedFolder))
            {
                watchedFolder = new WatchedFolder(directoryPath, RemoveEmptyWatcher);
                _watchedFolders[directoryPath] = watchedFolder;
            }

            subscription = watchedFolder.Subscribe(relativePath);
        }

        await using (subscription.ConfigureAwait(false))
        {
            await foreach (var changeEvent in subscription.ReadAllAsync(cancellationToken))
            {
                yield return changeEvent;
            }
        }
    }

    public void Dispose()
    {
        WatchedFolder[] watchedFolders;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            watchedFolders = _watchedFolders.Values.ToArray();
            _watchedFolders.Clear();
        }

        foreach (var watchedFolder in watchedFolders)
        {
            watchedFolder.Dispose();
        }
    }

    private void RemoveEmptyWatcher(WatchedFolder watchedFolder)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            if (_watchedFolders.TryGetValue(watchedFolder.DirectoryPath, out var current) &&
                ReferenceEquals(current, watchedFolder) &&
                watchedFolder.HasNoSubscribers)
            {
                _watchedFolders.Remove(watchedFolder.DirectoryPath);
                watchedFolder.Dispose();
            }
        }
    }

    private sealed class WatchedFolder : IDisposable
    {
        private const int SubscriberCapacity = 1;
        private readonly object _gate = new();
        private readonly Action<WatchedFolder> _removeWhenEmpty;
        private readonly Dictionary<Guid, Channel<PublicFolderChangeEvent>> _subscribers = [];
        private readonly FileSystemWatcher _watcher;
        private readonly System.Threading.Timer _debounceTimer;
        private bool _disposed;
        private long _version;

        public WatchedFolder(string directoryPath, Action<WatchedFolder> removeWhenEmpty)
        {
            DirectoryPath = directoryPath;
            _removeWhenEmpty = removeWhenEmpty;
            _watcher = new FileSystemWatcher(directoryPath)
            {
                IncludeSubdirectories = false,
                NotifyFilter =
                    NotifyFilters.FileName |
                    NotifyFilters.DirectoryName |
                    NotifyFilters.LastWrite |
                    NotifyFilters.Size,
            };
            _watcher.Changed += HandleFileSystemChange;
            _watcher.Created += HandleFileSystemChange;
            _watcher.Deleted += HandleFileSystemChange;
            _watcher.Renamed += HandleFileSystemChange;
            _watcher.Error += HandleWatcherError;
            _watcher.EnableRaisingEvents = true;
            _debounceTimer = new System.Threading.Timer(PublishDebouncedChange);
        }

        public string DirectoryPath { get; }

        public bool HasNoSubscribers
        {
            get
            {
                lock (_gate)
                {
                    return _subscribers.Count == 0;
                }
            }
        }

        public FolderSubscription Subscribe(string relativePath)
        {
            var subscriberId = Guid.NewGuid();
            var channel = Channel.CreateBounded<PublicFolderChangeEvent>(new BoundedChannelOptions(SubscriberCapacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false,
            });

            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                _subscribers[subscriberId] = channel;
            }

            return new FolderSubscription(this, subscriberId, relativePath, channel);
        }

        public void Dispose()
        {
            Channel<PublicFolderChangeEvent>[] subscribers;
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                subscribers = _subscribers.Values.ToArray();
                _subscribers.Clear();
            }

            _watcher.EnableRaisingEvents = false;
            _watcher.Changed -= HandleFileSystemChange;
            _watcher.Created -= HandleFileSystemChange;
            _watcher.Deleted -= HandleFileSystemChange;
            _watcher.Renamed -= HandleFileSystemChange;
            _watcher.Error -= HandleWatcherError;
            _watcher.Dispose();
            _debounceTimer.Dispose();

            foreach (var channel in subscribers)
            {
                channel.Writer.TryComplete();
            }
        }

        private void RemoveSubscriber(Guid subscriberId)
        {
            lock (_gate)
            {
                _subscribers.Remove(subscriberId, out var channel);
                channel?.Writer.TryComplete();
            }

            _removeWhenEmpty(this);
        }

        private void HandleFileSystemChange(object sender, FileSystemEventArgs args) => QueueChange();

        private void HandleWatcherError(object sender, ErrorEventArgs args) => QueueChange();

        private void QueueChange()
        {
            lock (_gate)
            {
                if (_disposed || _subscribers.Count == 0)
                {
                    return;
                }

                _debounceTimer.Change(ChangeDebounceDelay, Timeout.InfiniteTimeSpan);
            }
        }

        private void PublishDebouncedChange(object? state)
        {
            KeyValuePair<Guid, Channel<PublicFolderChangeEvent>>[] subscribers;
            long version;
            lock (_gate)
            {
                if (_disposed || _subscribers.Count == 0)
                {
                    return;
                }

                version = Interlocked.Increment(ref _version);
                subscribers = _subscribers.ToArray();
            }

            List<Guid>? completedSubscribers = null;
            foreach (var (subscriberId, channel) in subscribers)
            {
                if (!channel.Writer.TryWrite(new PublicFolderChangeEvent("folder-changed", string.Empty, version, DateTimeOffset.UtcNow)))
                {
                    completedSubscribers ??= [];
                    completedSubscribers.Add(subscriberId);
                }
            }

            if (completedSubscribers is not null)
            {
                lock (_gate)
                {
                    foreach (var subscriberId in completedSubscribers)
                    {
                        _subscribers.Remove(subscriberId);
                    }
                }
            }

            _removeWhenEmpty(this);
        }

        public sealed class FolderSubscription(WatchedFolder owner, Guid subscriberId, string relativePath, Channel<PublicFolderChangeEvent> channel) : IAsyncDisposable
        {
            public async IAsyncEnumerable<PublicFolderChangeEvent> ReadAllAsync([EnumeratorCancellation] CancellationToken cancellationToken)
            {
                await foreach (var changeEvent in channel.Reader.ReadAllAsync(cancellationToken))
                {
                    yield return changeEvent with { RelativePath = relativePath };
                }
            }

            public ValueTask DisposeAsync()
            {
                owner.RemoveSubscriber(subscriberId);
                return ValueTask.CompletedTask;
            }
        }
    }
}

internal sealed record PublicFolderChangeEvent(
    string Type,
    string RelativePath,
    long Version,
    DateTimeOffset OccurredAtUtc);
