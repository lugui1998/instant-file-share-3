using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using InstantFileShare.Agent;
using InstantFileShare.Core;
using InstantFileShare.Data;

namespace InstantFileShare.E2EHost;

internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static async Task<int> Main(string[] args)
    {
        var options = E2EHostOptions.Parse(args);
        var rootPath = Path.Combine(options.TempRoot, Guid.NewGuid().ToString("N"));
        var filesDirectory = Path.Combine(rootPath, "files");
        var receiveDirectory = Path.Combine(rootPath, "received");
        var logsDirectory = Path.Combine(rootPath, "logs");
        Directory.CreateDirectory(filesDirectory);
        Directory.CreateDirectory(receiveDirectory);
        Directory.CreateDirectory(logsDirectory);

        using var shutdown = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            shutdown.Cancel();
        };
        _ = Task.Run(async () =>
        {
            while (!shutdown.IsCancellationRequested)
            {
                var line = await Console.In.ReadLineAsync();
                if (line is null || string.Equals(line.Trim(), "stop", StringComparison.OrdinalIgnoreCase))
                {
                    shutdown.Cancel();
                    return;
                }
            }
        });

        try
        {
            var databasePath = Path.Combine(rootPath, "instant-file-share-e2e.db");
            var store = new SqliteShareStore(databasePath);
            await store.InitializeAsync(shutdown.Token);

            var publicPort = GetFreePort();
            var localPort = GetFreePort();
            var publicBaseUrl = $"http://127.0.0.1:{publicPort}";
            var settings = new AppSettings
            {
                DefaultPublishMode = PublishMode.Manual,
                ManualBindAddress = "127.0.0.1",
                ManualPublicPort = publicPort,
                LocalApiPort = localPort,
                ManualBaseUrl = publicBaseUrl,
                SendMetadataToCrawlers = true,
                StartOnLogin = false,
                OpenDashboardOnStart = false,
                ReceivePageTitle = "Upload to E2E Host",
                ReceiveUploadMode = ReceiveUploadMode.MultipartChunks,
                ReceiveUploadChunkSizeBytes = Defaults.MinimumReceiveUploadChunkSizeBytes,
                ReceiveUploadMaxBodySizeBytes = Defaults.DefaultReceiveUploadMaxBodySizeBytes,
                ReceiveParallelUploadLimit = 1,
                ReceiveNotificationsEnabled = false,
            };
            await store.SaveSettingsAsync(settings, shutdown.Token);

            var downloadPath = Path.Combine(filesDirectory, "download-smoke.txt");
            await File.WriteAllTextAsync(downloadPath, "download smoke from e2e host", shutdown.Token);
            await store.AddShareAsync(CreateFileShare("download-token", downloadPath, publicBaseUrl), shutdown.Token);
            await store.AddReceiveLinkAsync(CreateReceiveLink("receive-token", receiveDirectory, publicBaseUrl), shutdown.Token);

            var app = await global::Program.CreateAppAsync(
                [],
                new AgentApplicationOptions
                {
                    DatabasePath = databasePath,
                    LogsDirectory = logsDirectory,
                    RepositoryRoot = options.RepositoryRoot,
                    PublicShareAssetsDirectory = options.PublicShareAssetsDirectory,
                    BootstrapSettingsPath = Path.Combine(rootPath, "bootstrap-settings.json"),
                    RunStartupTasks = false,
                    EnableTrayIcon = false,
                    EnablePipeCommandServer = false,
                },
                shutdown.Token);

            await app.StartAsync(shutdown.Token);
            Console.WriteLine(JsonSerializer.Serialize(new E2EHostReady(
                PublicBaseUrl: publicBaseUrl,
                LocalBaseUrl: $"http://127.0.0.1:{localPort}",
                ReceiveUrl: $"{publicBaseUrl}/r/receive-token",
                DownloadUrl: $"{publicBaseUrl}/s/download-token",
                RootPath: rootPath,
                ReceiveDirectory: receiveDirectory,
                DownloadFilePath: downloadPath), JsonOptions));
            Console.Out.Flush();

            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, shutdown.Token);
            }
            catch (OperationCanceledException)
            {
            }

            using var stopTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await app.StopAsync(stopTimeout.Token);
            await app.DisposeAsync();
            return 0;
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
        finally
        {
            DeleteDirectoryEventually(rootPath);
        }
    }

    private static ShareRecord CreateFileShare(string token, string filePath, string publicBaseUrl)
    {
        var fileInfo = new FileInfo(filePath);
        return new ShareRecord
        {
            Id = Guid.NewGuid().ToString("N"),
            Token = token,
            FilePath = fileInfo.FullName,
            FileName = fileInfo.Name,
            Slug = fileInfo.Name,
            PublicBaseUrl = publicBaseUrl,
            FileSize = fileInfo.Length,
            FileModifiedAtUtc = new DateTimeOffset(fileInfo.LastWriteTimeUtc),
            CreatedAtUtc = DateTimeOffset.UtcNow,
            PublishMode = PublishMode.Manual,
            State = ShareState.Active,
            ShareKind = ShareKind.File,
        };
    }

    private static ReceiveLinkRecord CreateReceiveLink(string token, string folderPath, string publicBaseUrl)
    {
        var directoryInfo = new DirectoryInfo(folderPath);
        return new ReceiveLinkRecord
        {
            Id = Guid.NewGuid().ToString("N"),
            Token = token,
            TargetDirectoryPath = directoryInfo.FullName,
            TargetDisplayName = directoryInfo.Name,
            PublicBaseUrl = publicBaseUrl,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(1),
            MaxTotalBytes = Defaults.DefaultReceiveMaxTotalBytes,
            BytesReceived = 0,
            PublishMode = PublishMode.Manual,
            State = ReceiveLinkState.Active,
        };
    }

    private static int GetFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static void DeleteDirectoryEventually(string path)
    {
        if (!Directory.Exists(path) || Environment.GetEnvironmentVariable("IFS_E2E_KEEP_TEMP") == "1")
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
            catch (IOException)
            {
                return;
            }
            catch (UnauthorizedAccessException)
            {
                return;
            }
        }
    }

    private sealed record E2EHostReady(
        string PublicBaseUrl,
        string LocalBaseUrl,
        string ReceiveUrl,
        string DownloadUrl,
        string RootPath,
        string ReceiveDirectory,
        string DownloadFilePath);
}

internal sealed record E2EHostOptions(
    string RepositoryRoot,
    string PublicShareAssetsDirectory,
    string TempRoot)
{
    public static E2EHostOptions Parse(string[] args)
    {
        var repositoryRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var publicShareAssetsDirectory = Path.Combine(repositoryRoot, "src", "ui", "dist-public-share");
        var tempRoot = Path.Combine(repositoryRoot, ".tmp", "e2e");

        for (var index = 0; index < args.Length; index++)
        {
            var value = args[index];
            if (string.Equals(value, "--repository-root", StringComparison.OrdinalIgnoreCase) && index + 1 < args.Length)
            {
                repositoryRoot = Path.GetFullPath(args[++index]);
            }
            else if (string.Equals(value, "--public-share-assets", StringComparison.OrdinalIgnoreCase) && index + 1 < args.Length)
            {
                publicShareAssetsDirectory = Path.GetFullPath(args[++index]);
            }
            else if (string.Equals(value, "--temp-root", StringComparison.OrdinalIgnoreCase) && index + 1 < args.Length)
            {
                tempRoot = Path.GetFullPath(args[++index]);
            }
        }

        if (!Directory.Exists(publicShareAssetsDirectory))
        {
            throw new DirectoryNotFoundException($"Public-share assets directory was not found: {publicShareAssetsDirectory}");
        }

        Directory.CreateDirectory(tempRoot);
        return new E2EHostOptions(repositoryRoot, publicShareAssetsDirectory, tempRoot);
    }
}
