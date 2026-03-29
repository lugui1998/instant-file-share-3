using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using InstantFileShare.Agent;
using InstantFileShare.Core;
using InstantFileShare.Data;
using Microsoft.AspNetCore.Builder;

namespace InstantFileShare.Agent.Tests;

public sealed class AgentHttpIntegrationTests
{
    [Fact]
    public async Task FileShareDownload_ReturnsExpectedHeadersAndBody()
    {
        await using var host = await AgentTestHost.StartAsync(async context =>
        {
            var filePath = Path.Combine(context.FilesDirectory, "hello.txt");
            await File.WriteAllTextAsync(filePath, "hello world");
            await context.Store.AddShareAsync(context.CreateFileShare("file-token", filePath), CancellationToken.None);
        });

        using var response = await host.PublicClient.GetAsync("/s/file-token");

        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("attachment", response.Content.Headers.ContentDisposition?.DispositionType ?? response.Content.Headers.ContentType?.ToString() ?? response.Headers.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal("hello world", body);
    }

    [Fact]
    public async Task CrawlerRequest_ReturnsMetadataHtmlShell()
    {
        await using var host = await AgentTestHost.StartAsync(async context =>
        {
            var filePath = Path.Combine(context.FilesDirectory, "hello.txt");
            await File.WriteAllTextAsync(filePath, "hello world");
            await context.Store.AddShareAsync(context.CreateFileShare("file-token", filePath), CancellationToken.None);
        });

        using var request = new HttpRequestMessage(HttpMethod.Get, "/s/file-token");
        request.Headers.UserAgent.ParseAdd("Discordbot/2.0");

        using var response = await host.PublicClient.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html; charset=utf-8", response.Content.Headers.ContentType?.ToString());
        Assert.Contains("window.__IFS_PUBLIC_SHARE__", body);
        Assert.Contains("\"kind\":\"file\"", body);
        Assert.Contains("og:title", body);
    }

    [Fact]
    public async Task FolderBrowseRequest_ReturnsShellWithSerializedPageModel()
    {
        await using var host = await AgentTestHost.StartAsync(async context =>
        {
            var rootPath = Path.Combine(context.FilesDirectory, "team-files");
            Directory.CreateDirectory(Path.Combine(rootPath, "docs"));
            await File.WriteAllTextAsync(Path.Combine(rootPath, "docs", "guide.txt"), "guide");
            await context.Store.AddShareAsync(context.CreateFolderShare("folder-token", rootPath, "team-files"), CancellationToken.None);
        });

        using var response = await host.PublicClient.GetAsync("/s/folder-token/team-files/docs");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html; charset=utf-8", response.Content.Headers.ContentType?.ToString());
        Assert.Contains("\"kind\":\"folder\"", body);
        Assert.Contains("guide.txt", body);
        Assert.Contains("/s/folder-token/team-files/docs/guide.txt", body);
    }

    [Fact]
    public async Task FolderZipRequest_ReturnsZipForStandardClients()
    {
        await using var host = await AgentTestHost.StartAsync(async context =>
        {
            var rootPath = Path.Combine(context.FilesDirectory, "team-files");
            Directory.CreateDirectory(rootPath);
            await File.WriteAllTextAsync(Path.Combine(rootPath, "guide.txt"), "guide");
            await context.Store.AddShareAsync(context.CreateFolderShare("folder-token", rootPath, "team-files"), CancellationToken.None);
        });

        using var response = await host.PublicClient.GetAsync("/s/folder-token/team-files.zip");
        var zipBytes = await response.Content.ReadAsByteArrayAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/zip", response.Content.Headers.ContentType?.MediaType);

        using var zipStream = new MemoryStream(zipBytes);
        using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read);
        Assert.Contains(archive.Entries, entry => entry.FullName == "guide.txt");
    }

    [Fact]
    public async Task FolderBrowseSubdirectoryDownloadAll_ReturnsOnlyCurrentDirectoryAsZip()
    {
        await using var host = await AgentTestHost.StartAsync(async context =>
        {
            var rootPath = Path.Combine(context.FilesDirectory, "team-files");
            Directory.CreateDirectory(Path.Combine(rootPath, "docs"));
            Directory.CreateDirectory(Path.Combine(rootPath, "images"));
            await File.WriteAllTextAsync(Path.Combine(rootPath, "docs", "guide.txt"), "guide");
            await File.WriteAllTextAsync(Path.Combine(rootPath, "images", "logo.txt"), "logo");
            await context.Store.AddShareAsync(context.CreateFolderShare("folder-token", rootPath, "team-files"), CancellationToken.None);
        });

        using var response = await host.PublicClient.GetAsync("/s/folder-token/team-files/docs?download=zip");
        var zipBytes = await response.Content.ReadAsByteArrayAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/zip", response.Content.Headers.ContentType?.MediaType);

        using var zipStream = new MemoryStream(zipBytes);
        using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read);
        Assert.Contains(archive.Entries, entry => entry.FullName == "guide.txt");
        Assert.DoesNotContain(archive.Entries, entry => entry.FullName.StartsWith("docs/", StringComparison.Ordinal));
        Assert.DoesNotContain(archive.Entries, entry => entry.FullName.Contains("logo.txt", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FolderZipCrawlerRequest_ReturnsMetadataHtml()
    {
        await using var host = await AgentTestHost.StartAsync(async context =>
        {
            var rootPath = Path.Combine(context.FilesDirectory, "team-files");
            Directory.CreateDirectory(rootPath);
            await File.WriteAllTextAsync(Path.Combine(rootPath, "guide.txt"), "guide");
            await context.Store.AddShareAsync(context.CreateFolderShare("folder-token", rootPath, "team-files"), CancellationToken.None);
        });

        using var request = new HttpRequestMessage(HttpMethod.Get, "/s/folder-token/team-files.zip");
        request.Headers.UserAgent.ParseAdd("Discordbot/2.0");

        using var response = await host.PublicClient.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html; charset=utf-8", response.Content.Headers.ContentType?.ToString());
        Assert.Contains("\"kind\":\"zip\"", body);
    }

    [Theory]
    [InlineData("/s/folder-token/team-files/%2e%2e/secret.txt")]
    [InlineData("/s/folder-token/team-files/..%5csecret.txt")]
    [InlineData("/s/folder-token/team-files/..%2fsecret.txt")]
    [InlineData("/s/folder-token/team-files/docs/%2e%2e%5csecret.txt")]
    [InlineData("/s/folder-token/team-files/%252e%252e?download=zip")]
    [InlineData("/s/folder-token/team-files/%252e%252e/secret.txt")]
    [InlineData("/s/folder-token/team-files/docs/%252e%252e%255csecret.txt")]
    [InlineData("/s/folder-token/team-files/....//secret.txt")]
    [InlineData("/s/folder-token/team-files/....\\\\secret.txt")]
    [InlineData("/s/folder-token/team-files/C:%5cWindows%5cwin.ini")]
    [InlineData("/s/folder-token/team-files/%5c%5cserver%5cshare%5cfile.txt")]
    [InlineData("/s/folder-token/team-files/..%5csecret.txt%00")]
    [InlineData("/s/folder-token/team-files/guide.txt%00.pdf")]
    public async Task TraversalRequests_AreRejected(string requestPath)
    {
        await using var host = await AgentTestHost.StartAsync(async context =>
        {
            var rootPath = Path.Combine(context.FilesDirectory, "team-files");
            Directory.CreateDirectory(Path.Combine(rootPath, "docs"));
            await File.WriteAllTextAsync(Path.Combine(rootPath, "docs", "guide.txt"), "guide");
            await File.WriteAllTextAsync(Path.Combine(context.FilesDirectory, "secret.txt"), "secret");
            await context.Store.AddShareAsync(context.CreateFolderShare("folder-token", rootPath, "team-files"), CancellationToken.None);
        });

        using var response = await host.PublicClient.GetAsync(requestPath);

        Assert.Contains(response.StatusCode, new[] { HttpStatusCode.NotFound, HttpStatusCode.BadRequest });
    }

    [Fact]
    public async Task RawEncodedParentZipTraversalRequest_ReturnsNotFound()
    {
        await using var host = await AgentTestHost.StartAsync(async context =>
        {
            var rootPath = Path.Combine(context.FilesDirectory, "team-files");
            Directory.CreateDirectory(Path.Combine(rootPath, "docs"));
            await File.WriteAllTextAsync(Path.Combine(rootPath, "docs", "guide.txt"), "guide");
            await File.WriteAllTextAsync(Path.Combine(context.FilesDirectory, "secret.txt"), "secret");
            await context.Store.AddShareAsync(context.CreateFolderShare("folder-token", rootPath, "team-files"), CancellationToken.None);
        });

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, host.Settings.ManualPublicPort);
        using var stream = client.GetStream();

        var request =
            $"GET /s/folder-token/team-files/%2e%2e?download=zip HTTP/1.1\r\nHost: 127.0.0.1:{host.Settings.ManualPublicPort}\r\nConnection: close\r\n\r\n";
        var requestBytes = System.Text.Encoding.ASCII.GetBytes(request);
        await stream.WriteAsync(requestBytes);
        await stream.FlushAsync();

        using var reader = new StreamReader(stream, System.Text.Encoding.ASCII);
        var responseText = await reader.ReadToEndAsync();

        Assert.Contains("404", responseText, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/s/folder-token/team-files/%2e%2e?download=zip")]
    [InlineData("/s/folder-token/team-files/docs/%252e%252e?download=zip")]
    [InlineData("/s/folder-token/team-files/..%2f?download=zip")]
    public async Task RawTraversalZipRequests_ReturnNotFound(string rawTarget)
    {
        await using var host = await AgentTestHost.StartAsync(async context =>
        {
            var rootPath = Path.Combine(context.FilesDirectory, "team-files");
            Directory.CreateDirectory(Path.Combine(rootPath, "docs"));
            await File.WriteAllTextAsync(Path.Combine(rootPath, "docs", "guide.txt"), "guide");
            await File.WriteAllTextAsync(Path.Combine(context.FilesDirectory, "secret.txt"), "secret");
            await context.Store.AddShareAsync(context.CreateFolderShare("folder-token", rootPath, "team-files"), CancellationToken.None);
        });

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, host.Settings.ManualPublicPort);
        using var stream = client.GetStream();

        var request =
            $"GET {rawTarget} HTTP/1.1\r\nHost: 127.0.0.1:{host.Settings.ManualPublicPort}\r\nConnection: close\r\n\r\n";
        var requestBytes = System.Text.Encoding.ASCII.GetBytes(request);
        await stream.WriteAsync(requestBytes);
        await stream.FlushAsync();

        using var reader = new StreamReader(stream, System.Text.Encoding.ASCII);
        var responseText = await reader.ReadToEndAsync();

        Assert.Contains("404", responseText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ControlApi_IsForbiddenOnPublicListener()
    {
        await using var host = await AgentTestHost.StartAsync(_ => Task.CompletedTask);

        using var response = await host.PublicClient.GetAsync("/api/runtime");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    private sealed class AgentTestHost : IAsyncDisposable
    {
        private AgentTestHost(
            string rootPath,
            string filesDirectory,
            SqliteShareStore store,
            AppSettings settings,
            WebApplication app,
            HttpClient localClient,
            HttpClient publicClient)
        {
            RootPath = rootPath;
            FilesDirectory = filesDirectory;
            Store = store;
            Settings = settings;
            App = app;
            LocalClient = localClient;
            PublicClient = publicClient;
        }

        public string RootPath { get; }
        public string FilesDirectory { get; }
        public SqliteShareStore Store { get; }
        public AppSettings Settings { get; }
        public WebApplication App { get; }
        public HttpClient LocalClient { get; }
        public HttpClient PublicClient { get; }

        public static async Task<AgentTestHost> StartAsync(Func<SeedContext, Task> seedAsync)
        {
            var rootPath = Path.Combine(Path.GetTempPath(), "ifs-agent-http-tests", Guid.NewGuid().ToString("N"));
            var filesDirectory = Path.Combine(rootPath, "files");
            var logsDirectory = Path.Combine(rootPath, "logs");
            var assetsDirectory = Path.Combine(rootPath, "public-share");
            Directory.CreateDirectory(filesDirectory);
            Directory.CreateDirectory(logsDirectory);
            Directory.CreateDirectory(Path.Combine(assetsDirectory, ".vite"));
            Directory.CreateDirectory(Path.Combine(assetsDirectory, "assets"));

            await File.WriteAllTextAsync(
                Path.Combine(assetsDirectory, ".vite", "manifest.json"),
                """
                {
                  "src/public-share/main.ts": {
                    "file": "assets/public-share.js",
                    "isEntry": true,
                    "css": ["assets/public-share.css"]
                  }
                }
                """);
            await File.WriteAllTextAsync(Path.Combine(assetsDirectory, "assets", "public-share.js"), "console.log('public-share');");
            await File.WriteAllTextAsync(Path.Combine(assetsDirectory, "assets", "public-share.css"), "body{}");

            var databasePath = Path.Combine(rootPath, "instant-file-share.db");
            var store = new SqliteShareStore(databasePath);
            await store.InitializeAsync(CancellationToken.None);

            var settings = new AppSettings
            {
                DefaultPublishMode = PublishMode.Manual,
                ManualBindAddress = "127.0.0.1",
                ManualPublicPort = GetFreePort(),
                LocalApiPort = GetFreePort(),
                ManualBaseUrl = "http://127.0.0.1:0",
                SendMetadataToCrawlers = true,
                StartOnLogin = false,
                OpenDashboardOnStart = false,
            };
            settings = settings with
            {
                ManualBaseUrl = $"http://127.0.0.1:{settings.ManualPublicPort}",
            };
            await store.SaveSettingsAsync(settings, CancellationToken.None);

            var seedContext = new SeedContext(store, filesDirectory, settings);
            await seedAsync(seedContext);

            var app = await Program.CreateAppAsync(
                [],
                new AgentApplicationOptions
                {
                    DatabasePath = databasePath,
                    LogsDirectory = logsDirectory,
                    RepositoryRoot = Directory.GetCurrentDirectory(),
                    PublicShareAssetsDirectory = assetsDirectory,
                    RunStartupTasks = false,
                    EnableTrayIcon = false,
                    EnablePipeCommandServer = false,
                },
                CancellationToken.None);

            await app.StartAsync();

            return new AgentTestHost(
                rootPath,
                filesDirectory,
                store,
                settings,
                app,
                new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{settings.LocalApiPort}") },
                new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{settings.ManualPublicPort}") });
        }

        public async ValueTask DisposeAsync()
        {
            LocalClient.Dispose();
            PublicClient.Dispose();
            await App.StopAsync();
            await App.DisposeAsync();
            DeleteDirectoryEventually(RootPath);
        }

        private static int GetFreePort()
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return ((IPEndPoint)listener.LocalEndpoint).Port;
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
                catch (IOException)
                {
                    return;
                }
                catch (UnauthorizedAccessException)
                {
                    return;
                }
            }

            // Temp cleanup should not fail the test when Windows releases SQLite handles asynchronously.
        }
    }

    private sealed record SeedContext(SqliteShareStore Store, string FilesDirectory, AppSettings Settings)
    {
        public ShareRecord CreateFileShare(string token, string filePath)
        {
            var fileInfo = new FileInfo(filePath);
            return new ShareRecord
            {
                Id = Guid.NewGuid().ToString("N"),
                Token = token,
                FilePath = fileInfo.FullName,
                FileName = fileInfo.Name,
                Slug = fileInfo.Name,
                PublicBaseUrl = Settings.ManualBaseUrl ?? $"http://127.0.0.1:{Settings.ManualPublicPort}",
                FileSize = fileInfo.Length,
                FileModifiedAtUtc = new DateTimeOffset(fileInfo.LastWriteTimeUtc),
                CreatedAtUtc = DateTimeOffset.UtcNow,
                PublishMode = PublishMode.Manual,
                State = ShareState.Active,
                ShareKind = ShareKind.File,
            };
        }

        public ShareRecord CreateFolderShare(string token, string folderPath, string slug)
        {
            var directoryInfo = new DirectoryInfo(folderPath);
            return new ShareRecord
            {
                Id = Guid.NewGuid().ToString("N"),
                Token = token,
                FilePath = directoryInfo.FullName,
                FileName = directoryInfo.Name,
                Slug = slug,
                PublicBaseUrl = Settings.ManualBaseUrl ?? $"http://127.0.0.1:{Settings.ManualPublicPort}",
                FileSize = 0,
                FileModifiedAtUtc = new DateTimeOffset(directoryInfo.LastWriteTimeUtc),
                CreatedAtUtc = DateTimeOffset.UtcNow,
                PublishMode = PublishMode.Manual,
                State = ShareState.Active,
                ShareKind = ShareKind.Folder,
                CanBrowseFolderContents = true,
                CanDownloadFolderAsZip = true,
                PrimaryFolderEntryPoint = FolderShareEntryPoint.Browse,
            };
        }
    }
}
