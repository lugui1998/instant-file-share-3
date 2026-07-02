using System.IO.Compression;
using System.Net;
using System.Net.WebSockets;
using System.Net.Sockets;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using InstantFileShare.Agent;
using InstantFileShare.Core;
using InstantFileShare.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace InstantFileShare.Agent.Tests;

public sealed class AgentHttpIntegrationTests
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

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
    public async Task FileShareDownload_WithBrowserCompressionQuery_ReturnsGzipBody()
    {
        await using var host = await AgentTestHost.StartAsync(async context =>
        {
            var filePath = Path.Combine(context.FilesDirectory, "report.csv");
            await File.WriteAllTextAsync(filePath, "id,name\n1,Ada\n2,Grace\n");
            await context.Store.AddShareAsync(context.CreateFileShare("file-token", filePath), CancellationToken.None);
        });

        using var response = await host.PublicClient.GetAsync("/s/file-token?compression=gzip");
        await using var gzip = new GZipStream(await response.Content.ReadAsStreamAsync(), CompressionMode.Decompress);
        using var reader = new StreamReader(gzip);
        var body = await reader.ReadToEndAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("gzip", response.Headers.GetValues("X-IFS-Transfer-Compression").Single());
        Assert.Equal("22", response.Headers.GetValues("X-IFS-Uncompressed-Length").Single());
        Assert.Equal("id,name\n1,Ada\n2,Grace\n", body);
    }

    [Fact]
    public async Task FileShareDownload_WithBrowserCompressionRange_ReturnsGzipChunk()
    {
        await using var host = await AgentTestHost.StartAsync(async context =>
        {
            var filePath = Path.Combine(context.FilesDirectory, "report.csv");
            await File.WriteAllTextAsync(filePath, "id,name\n1,Ada\n2,Grace\n");
            await context.Store.AddShareAsync(context.CreateFileShare("file-token", filePath), CancellationToken.None);
        });

        using var request = new HttpRequestMessage(HttpMethod.Get, "/s/file-token?download=raw&compression=gzip");
        request.Headers.Range = new RangeHeaderValue(8, 12);

        using var response = await host.PublicClient.SendAsync(request);
        await using var gzip = new GZipStream(await response.Content.ReadAsStreamAsync(), CompressionMode.Decompress);
        using var reader = new StreamReader(gzip);
        var body = await reader.ReadToEndAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("gzip", response.Headers.GetValues("X-IFS-Transfer-Compression").Single());
        Assert.Equal("5", response.Headers.GetValues("X-IFS-Uncompressed-Length").Single());
        Assert.Equal("8", response.Headers.GetValues("X-IFS-Chunk-Start").Single());
        Assert.Equal("5", response.Headers.GetValues("X-IFS-Chunk-Size").Single());
        Assert.Equal("1,Ada", body);
    }

    [Fact]
    public async Task EncryptedDownloadPlan_RejectsLiveFileSharesWithoutIvMetadata()
    {
        await using var host = await AgentTestHost.StartAsync(async context =>
        {
            var filePath = Path.Combine(context.FilesDirectory, "secret.bin");
            await File.WriteAllBytesAsync(filePath, [1, 2, 3, 4]);
            await context.Store.AddShareAsync(context.CreateFileShare("file-token", filePath), CancellationToken.None);
        });

        using var response = await host.PublicClient.GetAsync("/s/file-token?ifs=encrypted-download-plan");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("no persisted AES-GCM IV metadata", body, StringComparison.Ordinal);
        Assert.DoesNotContain("ivBase64Url", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("AAAAAAAAAAAAAAAA", body, StringComparison.Ordinal);
        Assert.DoesNotContain("encryptedDownloadUrl", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task EncryptedDownload_RejectsLiveFileSharesInsteadOfServingPlaintextAsCiphertext()
    {
        await using var host = await AgentTestHost.StartAsync(async context =>
        {
            var filePath = Path.Combine(context.FilesDirectory, "secret.bin");
            await File.WriteAllBytesAsync(filePath, [1, 2, 3, 4]);
            await context.Store.AddShareAsync(context.CreateFileShare("file-token", filePath), CancellationToken.None);
        });

        using var response = await host.PublicClient.GetAsync("/s/file-token?ifs=encrypted-download");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("no persisted AES-GCM IV metadata", body, StringComparison.Ordinal);
        Assert.DoesNotContain("AQIDBA", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FileShareDownload_WhenEncryptionAlways_BlocksRawDownload()
    {
        await using var host = await AgentTestHost.StartAsync(async context =>
        {
            var filePath = Path.Combine(context.FilesDirectory, "secret.txt");
            await File.WriteAllTextAsync(filePath, "plain secret");
            await context.Store.SaveSettingsAsync(
                context.Settings with { BrowserDownloadEncryptionPolicy = BrowserTransferEncryptionPolicy.Always },
                CancellationToken.None);
            await context.Store.AddShareAsync(context.CreateFileShare("file-token", filePath), CancellationToken.None);
        });

        using var response = await host.PublicClient.GetAsync("/s/file-token?download=raw");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("requires browser-managed encryption", body, StringComparison.Ordinal);
        Assert.DoesNotContain("plain secret", body, StringComparison.Ordinal);
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
        Assert.Contains("\"repositoryUrl\":\"https://github.com/lugui1998/instant-file-share-3\"", body);
        Assert.Contains("og:title", body);
        Assert.Contains("<link rel=\"icon\" href=\"/favicon.ico\" />", body);
    }

    [Fact]
    public async Task BrowserManagedDownloadPageRequest_ReturnsShellForNonInlineBrowserClients_AndKeepsRawFallback()
    {
        await using var host = await AgentTestHost.StartAsync(async context =>
        {
            var filePath = Path.Combine(context.FilesDirectory, "archive.bin");
            await File.WriteAllTextAsync(filePath, "managed body");
            await context.Store.AddShareAsync(context.CreateFileShare("managed-token", filePath), CancellationToken.None);
        });

        using var rawResponse = await host.PublicClient.GetAsync("/s/managed-token");
        var rawBody = await rawResponse.Content.ReadAsStringAsync();
        using var browserRequest = new HttpRequestMessage(HttpMethod.Get, "/s/managed-token");
        browserRequest.Headers.Accept.ParseAdd("text/html");
        using var pageResponse = await host.PublicClient.SendAsync(browserRequest);
        var pageBody = await pageResponse.Content.ReadAsStringAsync();
        using var rawBrowserRequest = new HttpRequestMessage(HttpMethod.Get, "/s/managed-token?download=raw");
        rawBrowserRequest.Headers.Accept.ParseAdd("text/html");
        using var rawBrowserResponse = await host.PublicClient.SendAsync(rawBrowserRequest);
        var rawBrowserBody = await rawBrowserResponse.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, rawResponse.StatusCode);
        Assert.Equal("managed body", rawBody);
        Assert.Equal(HttpStatusCode.OK, pageResponse.StatusCode);
        Assert.Equal("text/html; charset=utf-8", pageResponse.Content.Headers.ContentType?.ToString());
        Assert.Contains("\"managedDownload\"", pageBody);
        Assert.Contains("\"manifestUrl\":\"/s/managed-token?ifs=download-plan\"", pageBody);
        Assert.Contains("ifs=download-plan", pageBody);
        Assert.Contains("\"rawDownloadUrl\":\"/s/managed-token?download=raw\"", pageBody);
        Assert.Contains("download=raw", pageBody);
        Assert.Equal(HttpStatusCode.OK, rawBrowserResponse.StatusCode);
        Assert.Equal("managed body", rawBrowserBody);
    }

    [Fact]
    public async Task BrowserManagedDownloadPageRequest_DoesNotReplaceInlineBrowserContent()
    {
        await using var host = await AgentTestHost.StartAsync(async context =>
        {
            var filePath = Path.Combine(context.FilesDirectory, "paper.pdf");
            await File.WriteAllTextAsync(filePath, "%PDF-1.7");
            await context.Store.AddShareAsync(context.CreateFileShare("pdf-token", filePath), CancellationToken.None);
        });

        using var request = new HttpRequestMessage(HttpMethod.Get, "/s/pdf-token");
        request.Headers.Accept.ParseAdd("text/html");
        using var response = await host.PublicClient.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/pdf", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("%PDF-1.7", body);
    }

    [Theory]
    [InlineData(true, "video/x-matroska")]
    [InlineData(false, "text/html")]
    public async Task BrowserManagedDownloadPageRequest_RespectsOpenVideosSettingForMkv(bool openVideosInBrowser, string expectedContentType)
    {
        await using var host = await AgentTestHost.StartAsync(async context =>
        {
            await context.Store.SaveSettingsAsync(
                context.Settings with { OpenVideosInBrowser = openVideosInBrowser },
                CancellationToken.None);
            var filePath = Path.Combine(context.FilesDirectory, "clip.mkv");
            await File.WriteAllTextAsync(filePath, "mkv body");
            await context.Store.AddShareAsync(context.CreateFileShare("mkv-token", filePath), CancellationToken.None);
        });

        using var request = new HttpRequestMessage(HttpMethod.Get, "/s/mkv-token");
        request.Headers.Accept.ParseAdd("text/html");
        using var response = await host.PublicClient.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(expectedContentType, response.Content.Headers.ContentType?.MediaType);
        if (openVideosInBrowser)
        {
            Assert.Equal("mkv body", body);
            Assert.Equal("inline", response.Content.Headers.ContentDisposition?.DispositionType);
        }
        else
        {
            Assert.Contains("window.__IFS_PUBLIC_SHARE__", body);
            Assert.Contains("\"managedDownload\"", body);
        }
    }

    [Fact]
    public async Task BrowserManagedDownloadPlan_ReturnsRangeChunksWithSha256Integrity()
    {
        var bytes = Enumerable.Range(0, 70000).Select(index => (byte)(index % 251)).ToArray();
        await using var host = await AgentTestHost.StartAsync(async context =>
        {
            var filePath = Path.Combine(context.FilesDirectory, "chunks.bin");
            await File.WriteAllBytesAsync(filePath, bytes);
            await context.Store.AddShareAsync(context.CreateFileShare("chunks-token", filePath), CancellationToken.None);
        });

        using var response = await host.PublicClient.GetAsync("/s/chunks-token?ifs=download-plan&chunkSize=65536");
        var plan = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json; charset=utf-8", response.Content.Headers.ContentType?.ToString());
        Assert.Equal("chunks.bin", plan.GetProperty("fileName").GetString());
        Assert.Equal(bytes.Length, plan.GetProperty("fileSizeBytes").GetInt64());
        Assert.Equal("application/octet-stream", plan.GetProperty("contentType").GetString());
        Assert.Equal("bytes", plan.GetProperty("rangeUnit").GetString());
        Assert.Equal("sha-256", plan.GetProperty("integrityAlgorithm").GetString());
        Assert.True(plan.GetProperty("lastModifiedUtcTicks").GetInt64() > 0);
        Assert.False(string.IsNullOrWhiteSpace(plan.GetProperty("lastModifiedUtc").GetString()));
        Assert.Equal(64, plan.GetProperty("planHash").GetString()?.Length);
        Assert.Equal(64, plan.GetProperty("planSignature").GetString()?.Length);
        Assert.Equal(65536, plan.GetProperty("chunkSizeBytes").GetInt32());
        Assert.Contains("download=raw", plan.GetProperty("rawDownloadUrl").GetString());
        Assert.StartsWith("/s/chunks-token?", plan.GetProperty("rawDownloadUrl").GetString());
        Assert.Contains("ifsPlanHash=", plan.GetProperty("rawDownloadUrl").GetString());
        Assert.Contains("ifsPlanSignature=", plan.GetProperty("rawDownloadUrl").GetString());
        Assert.Contains("ifsPlanSize=", plan.GetProperty("rawDownloadUrl").GetString());
        Assert.Contains("ifsPlanModified=", plan.GetProperty("rawDownloadUrl").GetString());
        Assert.Contains("chunkSize=65536", plan.GetProperty("rawDownloadUrl").GetString());

        var chunks = plan.GetProperty("chunks").EnumerateArray().ToArray();
        Assert.Equal(2, chunks.Length);
        Assert.Equal(0, chunks[0].GetProperty("start").GetInt64());
        Assert.Equal(65535, chunks[0].GetProperty("end").GetInt64());
        Assert.Equal(65536, chunks[0].GetProperty("sizeBytes").GetInt64());
        Assert.Equal(ComputeSha256Hex(bytes, 0, 65536), chunks[0].GetProperty("sha256").GetString());
        Assert.Equal(65536, chunks[1].GetProperty("start").GetInt64());
        Assert.Equal(69999, chunks[1].GetProperty("end").GetInt64());
        Assert.Equal(4464, chunks[1].GetProperty("sizeBytes").GetInt64());
        Assert.Equal(ComputeSha256Hex(bytes, 65536, 4464), chunks[1].GetProperty("sha256").GetString());
    }

    [Fact]
    public async Task BrowserManagedDownloadPlan_RawChunkRequestRejectsTamperedSignature()
    {
        await using var host = await AgentTestHost.StartAsync(async context =>
        {
            var filePath = Path.Combine(context.FilesDirectory, "signed.bin");
            await File.WriteAllBytesAsync(filePath, [1, 2, 3, 4]);
            await context.Store.AddShareAsync(context.CreateFileShare("signed-token", filePath), CancellationToken.None);
        });

        using var planResponse = await host.PublicClient.GetAsync("/s/signed-token?ifs=download-plan&chunkSize=65536");
        var plan = await planResponse.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        var rawDownloadUrl = plan.GetProperty("rawDownloadUrl").GetString();
        var planSignature = plan.GetProperty("planSignature").GetString();
        var tamperedRawDownloadUrl = rawDownloadUrl!.Replace(
            $"ifsPlanSignature={planSignature}",
            $"ifsPlanSignature={new string('0', 64)}",
            StringComparison.Ordinal);

        using var request = new HttpRequestMessage(HttpMethod.Get, tamperedRawDownloadUrl);
        request.Headers.Range = new RangeHeaderValue(0, 3);
        using var response = await host.PublicClient.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("browser-managed download plan", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("AQIDBA", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BrowserManagedDownloadPlan_RawChunkRequestRejectsStaleLiveFile()
    {
        var filePath = string.Empty;
        await using var host = await AgentTestHost.StartAsync(async context =>
        {
            filePath = Path.Combine(context.FilesDirectory, "live.bin");
            await File.WriteAllBytesAsync(filePath, [1, 2, 3, 4]);
            await context.Store.SaveSettingsAsync(context.Settings with { FileChangeBehavior = FileChangeBehavior.Lenient }, CancellationToken.None);
            await context.Store.AddShareAsync(context.CreateFileShare("live-token", filePath), CancellationToken.None);
        });

        using var planResponse = await host.PublicClient.GetAsync("/s/live-token?ifs=download-plan&chunkSize=65536");
        var plan = await planResponse.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        var rawDownloadUrl = plan.GetProperty("rawDownloadUrl").GetString();

        await Task.Delay(20);
        await File.WriteAllBytesAsync(filePath, [9, 8, 7, 6]);
        File.SetLastWriteTimeUtc(filePath, DateTime.UtcNow.AddSeconds(1));

        using var request = new HttpRequestMessage(HttpMethod.Get, rawDownloadUrl);
        request.Headers.Range = new RangeHeaderValue(0, 3);
        using var response = await host.PublicClient.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("changed after the browser-managed download plan was created", body);
    }

    [Fact]
    public async Task FaviconRequest_ReturnsApplicationIcon()
    {
        await using var host = await AgentTestHost.StartAsync(_ => Task.CompletedTask);

        using var response = await host.PublicClient.GetAsync("/favicon.ico");
        var body = await response.Content.ReadAsByteArrayAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/x-icon", response.Content.Headers.ContentType?.ToString());
        Assert.NotEmpty(body);
    }

    [Fact]
    public async Task FolderBrowseRequest_ReturnsShellWithSerializedPageModel()
    {
        await using var host = await AgentTestHost.StartAsync(async context =>
        {
            var rootPath = Path.Combine(context.FilesDirectory, "team-files");
            Directory.CreateDirectory(Path.Combine(rootPath, "docs"));
            await File.WriteAllTextAsync(Path.Combine(rootPath, "docs", "guide.txt"), "guide");
            await context.Store.SaveSettingsAsync(context.Settings with { FolderBrowsePageTitle = "Browse files from Desk" }, CancellationToken.None);
            await context.Store.AddShareAsync(context.CreateFolderShare("folder-token", rootPath, "team-files"), CancellationToken.None);
        });

        using var response = await host.PublicClient.GetAsync("/s/folder-token/docs");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html; charset=utf-8", response.Content.Headers.ContentType?.ToString());
        Assert.Contains("\"kind\":\"folder\"", body);
        Assert.Contains("\"title\":\"Browse files from Desk\"", body);
        Assert.Contains("\"repositoryUrl\":\"https://github.com/lugui1998/instant-file-share-3\"", body);
        Assert.DoesNotContain("\"title\":\"docs\"", body);
        Assert.Contains("guide.txt", body);
        Assert.Contains("/s/folder-token/docs/guide.txt", body);
    }

    [Fact]
    public async Task FolderBrowseListRequest_ReturnsCurrentFolderModelAsJson()
    {
        await using var host = await AgentTestHost.StartAsync(async context =>
        {
            var rootPath = Path.Combine(context.FilesDirectory, "team-files");
            Directory.CreateDirectory(Path.Combine(rootPath, "docs"));
            await File.WriteAllTextAsync(Path.Combine(rootPath, "docs", "guide.txt"), "guide");
            await context.Store.AddShareAsync(context.CreateFolderShare("folder-token", rootPath, "team-files"), CancellationToken.None);
        });

        await File.WriteAllTextAsync(Path.Combine(host.FilesDirectory, "team-files", "docs", "new-file.txt"), "new");
        await File.WriteAllTextAsync(Path.Combine(host.FilesDirectory, "team-files", "docs", "large.bin.downloadpart"), "partial");

        using var response = await host.PublicClient.GetAsync("/s/folder-token/docs?ifs=folder-list");
        var folder = await response.Content.ReadFromJsonAsync<PublicShareFolderModel>(JsonOptions);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json; charset=utf-8", response.Content.Headers.ContentType?.ToString());
        Assert.NotNull(folder);
        Assert.Equal("docs", folder.RelativePath);
        Assert.Contains(folder.Entries, entry => entry.Name == "guide.txt");
        Assert.Contains(folder.Entries, entry => entry.Name == "new-file.txt");
        Assert.DoesNotContain(folder.Entries, entry => entry.Name == "large.bin.downloadpart");
    }

    [Fact]
    public async Task FolderZipRequest_ReturnsZipForStandardClients()
    {
        const string guideText = "guide";
        await using var host = await AgentTestHost.StartAsync(async context =>
        {
            var rootPath = Path.Combine(context.FilesDirectory, "team-files");
            Directory.CreateDirectory(rootPath);
            await File.WriteAllTextAsync(Path.Combine(rootPath, "guide.txt"), guideText);
            await context.Store.AddShareAsync(context.CreateFolderShare("folder-token", rootPath, "team-files"), CancellationToken.None);
        });

        using var response = await host.PublicClient.GetAsync("/s/folder-token?download=zip");
        var zipBytes = await response.Content.ReadAsByteArrayAsync();
        var coordinator = host.App.Services.GetRequiredService<IShareCoordinator>();
        var transfers = await coordinator.GetTransfersAsync(CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/zip", response.Content.Headers.ContentType?.MediaType);

        using var zipStream = new MemoryStream(zipBytes);
        using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read);
        Assert.Contains(archive.Entries, entry => entry.FullName == "guide.txt");
        var transfer = Assert.Single(transfers, entry => entry.TransferKind == TransferKind.FolderZipDownload);
        Assert.Equal(zipBytes.Length, transfer.BytesSent);
        Assert.Equal(zipBytes.Length, transfer.TotalBytes);
        Assert.Equal(guideText.Length, transfer.ProgressBytes);
        Assert.Equal(guideText.Length, transfer.ProgressTotalBytes);
        Assert.True(transfer.Succeeded);
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

        using var response = await host.PublicClient.GetAsync("/s/folder-token/docs?download=zip");
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

        using var request = new HttpRequestMessage(HttpMethod.Get, "/s/folder-token?download=zip");
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
    [InlineData("/s/folder-token/team-files/%ZZ")]
    [InlineData("/s/folder-token/team-files/%")]
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

    [Fact]
    public async Task RangeDownload_CountsAsAUse_AndExpiresMaxUseShare()
    {
        await using var host = await AgentTestHost.StartAsync(async context =>
        {
            var filePath = Path.Combine(context.FilesDirectory, "range-max-uses.txt");
            await File.WriteAllTextAsync(filePath, "hello world");
            await context.Store.AddShareAsync(context.CreateFileShare("range-max-token", filePath) with
            {
                MaxUses = 1,
            }, CancellationToken.None);
        });

        using var rangeRequest = new HttpRequestMessage(HttpMethod.Get, "/s/range-max-token");
        rangeRequest.Headers.Range = new RangeHeaderValue(0, 3);

        using var firstResponse = await host.PublicClient.SendAsync(rangeRequest);
        _ = await firstResponse.Content.ReadAsByteArrayAsync();
        using var secondResponse = await host.PublicClient.GetAsync("/s/range-max-token");
        _ = await secondResponse.Content.ReadAsByteArrayAsync();

        var share = await host.Store.GetShareByTokenAsync("range-max-token", CancellationToken.None);

        Assert.Equal(HttpStatusCode.PartialContent, firstResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Gone, secondResponse.StatusCode);
        Assert.NotNull(share);
        Assert.Equal(1, share!.UseCount);
    }

    [Fact]
    public async Task FolderFileRangeDownload_UsesSessionDeduplication()
    {
        await using var host = await AgentTestHost.StartAsync(async context =>
        {
            var rootPath = Path.Combine(context.FilesDirectory, "range-folder");
            Directory.CreateDirectory(rootPath);
            await File.WriteAllTextAsync(Path.Combine(rootPath, "guide.txt"), "hello world");
            await context.Store.AddShareAsync(context.CreateFolderShare("range-folder-token", rootPath, "range-folder") with
            {
                MaxUses = 5,
            }, CancellationToken.None);
        });

        using var handler = new HttpClientHandler
        {
            CookieContainer = new CookieContainer(),
            UseCookies = true,
        };
        using var client = new HttpClient(handler)
        {
            BaseAddress = new Uri($"http://127.0.0.1:{host.Settings.ManualPublicPort}"),
        };

        using var firstRequest = new HttpRequestMessage(HttpMethod.Get, "/s/range-folder-token/range-folder/guide.txt");
        firstRequest.Headers.Range = new RangeHeaderValue(0, 3);
        using var secondRequest = new HttpRequestMessage(HttpMethod.Get, "/s/range-folder-token/range-folder/guide.txt");
        secondRequest.Headers.Range = new RangeHeaderValue(4, 7);

        using var firstResponse = await client.SendAsync(firstRequest);
        using var secondResponse = await client.SendAsync(secondRequest);
        _ = await firstResponse.Content.ReadAsByteArrayAsync();
        _ = await secondResponse.Content.ReadAsByteArrayAsync();
        var share = await host.Store.GetShareByTokenAsync("range-folder-token", CancellationToken.None);

        Assert.Equal(HttpStatusCode.PartialContent, firstResponse.StatusCode);
        Assert.Equal(HttpStatusCode.PartialContent, secondResponse.StatusCode);
        Assert.NotNull(share);
        Assert.Equal(1, share!.UseCount);
    }

    [Fact]
    public async Task ReceiveLinkPage_ReturnsShellWithSerializedPageModel()
    {
        await using var host = await AgentTestHost.StartAsync(async context =>
        {
            var dropPath = Path.Combine(context.FilesDirectory, "drop");
            Directory.CreateDirectory(dropPath);
            await context.Store.SaveSettingsAsync(context.Settings with
            {
                ReceivePageTitle = "Upload to Desk",
                ReceiveUploadMode = ReceiveUploadMode.BinaryChunks,
                ReceiveUploadChunkSizeBytes = 8 * 1024 * 1024,
            }, CancellationToken.None);
            await context.Store.AddReceiveLinkAsync(context.CreateReceiveLink("receive-token", dropPath), CancellationToken.None);
        });

        using var response = await host.PublicClient.GetAsync("/r/receive-token");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html; charset=utf-8", response.Content.Headers.ContentType?.ToString());
        Assert.Contains("\"kind\":\"receive\"", body);
        Assert.Contains("\"title\":\"Upload to Desk\"", body);
        Assert.Contains("\"uploadUrl\":\"/r/receive-token\"", body);
        Assert.Contains("\"uploadEventsUrl\":\"/r/receive-token/events\"", body);
        Assert.Contains("\"uploadMode\":\"BinaryChunks\"", body);
        Assert.Contains("\"uploadCompressionEnabled\":true", body);
        Assert.Contains("\"uploadChunkSizeBytes\":8388608", body);
        Assert.DoesNotContain("Upload to drop", body);
        Assert.Contains("window.__IFS_PUBLIC_SHARE__", body);
    }

    [Fact]
    public async Task ReceiveUpload_SavesMixedFiles_AndRenamesConflictingRootFolder()
    {
        await using var host = await AgentTestHost.StartAsync(async context =>
        {
            var dropPath = Path.Combine(context.FilesDirectory, "drop");
            Directory.CreateDirectory(dropPath);
            Directory.CreateDirectory(Path.Combine(dropPath, "Photos"));
            await context.Store.AddReceiveLinkAsync(context.CreateReceiveLink("receive-token", dropPath), CancellationToken.None);
        });

        using var form = new MultipartFormDataContent();
        form.Add(new StringContent("hello.txt"), "relativePaths");
        form.Add(new StringContent("5"), "fileSizes");
        form.Add(CreateFileContent("hello"), "files", "hello.txt");
        form.Add(new StringContent("Photos/cover.jpg"), "relativePaths");
        form.Add(new StringContent("5"), "fileSizes");
        form.Add(CreateFileContent("image"), "files", "cover.jpg");
        form.Add(new StringContent("Photos/docs/guide.txt"), "relativePaths");
        form.Add(new StringContent("5"), "fileSizes");
        form.Add(CreateFileContent("guide"), "files", "guide.txt");

        using var response = await host.PublicClient.PostAsync("/r/receive-token", form);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("hello", await File.ReadAllTextAsync(Path.Combine(host.FilesDirectory, "drop", "hello.txt")));
        Assert.Equal("image", await File.ReadAllTextAsync(Path.Combine(host.FilesDirectory, "drop", "Photos (1)", "cover.jpg")));
        Assert.Equal("guide", await File.ReadAllTextAsync(Path.Combine(host.FilesDirectory, "drop", "Photos (1)", "docs", "guide.txt")));
        Assert.Contains("\"uploadedCount\":3", body);
        Assert.DoesNotContain("\"fileName\"", body);
        Assert.DoesNotContain("\"relativePath\"", body);
        Assert.DoesNotContain("\"storedRelativePath\"", body);
        AssertNoPartialUploads(Path.Combine(host.FilesDirectory, "drop"));
    }

    [Fact]
    public async Task ReceiveUpload_RejectsTraversalRelativePaths()
    {
        await using var host = await AgentTestHost.StartAsync(async context =>
        {
            var dropPath = Path.Combine(context.FilesDirectory, "drop");
            Directory.CreateDirectory(dropPath);
            await context.Store.AddReceiveLinkAsync(context.CreateReceiveLink("receive-token", dropPath), CancellationToken.None);
        });

        using var form = new MultipartFormDataContent();
        form.Add(new StringContent("../outside.txt"), "relativePaths");
        form.Add(new StringContent("3"), "fileSizes");
        form.Add(CreateFileContent("bad"), "files", "bad.txt");

        using var response = await host.PublicClient.PostAsync("/r/receive-token", form);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.DoesNotContain("outside.txt", Directory.GetFiles(host.FilesDirectory, "*", SearchOption.AllDirectories).Select(Path.GetFileName));
        Assert.Contains("\"failedCount\":1", body);
    }

    [Fact]
    public async Task ReceiveUpload_RejectsFilesThatExceedQuota()
    {
        await using var host = await AgentTestHost.StartAsync(async context =>
        {
            var dropPath = Path.Combine(context.FilesDirectory, "drop");
            Directory.CreateDirectory(dropPath);
            await context.Store.AddReceiveLinkAsync(context.CreateReceiveLinkWithQuota("receive-token", dropPath, 3), CancellationToken.None);
        });

        using var form = new MultipartFormDataContent();
        form.Add(new StringContent("hello.txt"), "relativePaths");
        form.Add(new StringContent("5"), "fileSizes");
        form.Add(CreateFileContent("hello"), "files", "hello.txt");

        using var response = await host.PublicClient.PostAsync("/r/receive-token", form);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.False(File.Exists(Path.Combine(host.FilesDirectory, "drop", "hello.txt")));
        AssertNoPartialUploads(Path.Combine(host.FilesDirectory, "drop"));
        Assert.Contains("\"uploadedCount\":0", body);
    }

    [Fact]
    public async Task ReceiveUpload_RejectsFilesThatEndBeforeDeclaredSize()
    {
        await using var host = await AgentTestHost.StartAsync(async context =>
        {
            var dropPath = Path.Combine(context.FilesDirectory, "drop");
            Directory.CreateDirectory(dropPath);
            await context.Store.AddReceiveLinkAsync(context.CreateReceiveLink("receive-token", dropPath), CancellationToken.None);
        });

        using var form = new MultipartFormDataContent();
        form.Add(new StringContent("partial.bin"), "relativePaths");
        form.Add(new StringContent("10"), "fileSizes");
        form.Add(CreateFileContent("short"), "files", "partial.bin");

        using var response = await host.PublicClient.PostAsync("/r/receive-token", form);
        var body = await response.Content.ReadAsStringAsync();
        using var transfersResponse = await host.LocalClient.GetAsync("/api/transfers");
        var transfers = await transfersResponse.Content.ReadFromJsonAsync<List<TransferSnapshot>>(JsonOptions);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(File.Exists(Path.Combine(host.FilesDirectory, "drop", "partial.bin")));
        AssertNoPartialUploads(Path.Combine(host.FilesDirectory, "drop"));
        Assert.Contains("\"failedCount\":1", body);
        Assert.Contains("The upload did not complete.", body);
        var uploadTransfer = Assert.Single(transfers!);
        Assert.Equal(TransferState.Failed, uploadTransfer.State);
        Assert.Equal(10, uploadTransfer.TotalBytes);
        Assert.Equal(5, uploadTransfer.ProgressBytes);
        Assert.Equal(10, uploadTransfer.ProgressTotalBytes);
    }

    [Fact]
    public async Task ReceiveUpload_AssemblesChunkedFileAcrossRequests()
    {
        await using var host = await AgentTestHost.StartAsync(async context =>
        {
            var dropPath = Path.Combine(context.FilesDirectory, "drop");
            Directory.CreateDirectory(dropPath);
            await context.Store.AddReceiveLinkAsync(context.CreateReceiveLink("receive-token", dropPath), CancellationToken.None);
        });

        using var firstChunkForm = CreateChunkUploadForm(
            uploadId: "upload-1",
            relativePath: "large.bin",
            totalBytes: 10,
            chunkIndex: 0,
            chunkCount: 2,
            chunkStart: 0,
            chunkValue: "hello");
        using var firstResponse = await host.PublicClient.PostAsync("/r/receive-token", firstChunkForm);
        var firstBody = await firstResponse.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
        Assert.Contains("\"uploadedCount\":0", firstBody);
        Assert.False(File.Exists(Path.Combine(host.FilesDirectory, "drop", "large.bin")));
        Assert.True(File.Exists(Path.Combine(host.FilesDirectory, "drop", "large.bin.downloadpart")));

        using var firstTransfersResponse = await host.LocalClient.GetAsync("/api/transfers");
        var firstTransfers = await firstTransfersResponse.Content.ReadFromJsonAsync<List<TransferSnapshot>>(JsonOptions);
        var activeTransfer = Assert.Single(firstTransfers!);
        Assert.Equal(TransferState.InProgress, activeTransfer.State);
        Assert.Equal(5, activeTransfer.ProgressBytes);
        Assert.Equal(10, activeTransfer.ProgressTotalBytes);

        using var secondChunkForm = CreateChunkUploadForm(
            uploadId: "upload-1",
            relativePath: "large.bin",
            totalBytes: 10,
            chunkIndex: 1,
            chunkCount: 2,
            chunkStart: 5,
            chunkValue: "world");
        using var secondResponse = await host.PublicClient.PostAsync("/r/receive-token", secondChunkForm);
        var secondBody = await secondResponse.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, secondResponse.StatusCode);
        Assert.Equal("helloworld", await File.ReadAllTextAsync(Path.Combine(host.FilesDirectory, "drop", "large.bin")));
        AssertNoPartialUploads(Path.Combine(host.FilesDirectory, "drop"));
        Assert.Contains("\"uploadedCount\":1", secondBody);

        using var completedTransfersResponse = await host.LocalClient.GetAsync("/api/transfers");
        var completedTransfers = await completedTransfersResponse.Content.ReadFromJsonAsync<List<TransferSnapshot>>(JsonOptions);
        var completedTransfer = Assert.Single(completedTransfers!);
        Assert.Equal(activeTransfer.Id, completedTransfer.Id);
        Assert.Equal(TransferState.Completed, completedTransfer.State);
        Assert.Equal(10, completedTransfer.ProgressBytes);
        Assert.Equal(10, completedTransfer.ProgressTotalBytes);
    }

    [Fact]
    public async Task ReceiveUpload_RetriesIncompleteChunkFromConfirmedBytes()
    {
        await using var host = await AgentTestHost.StartAsync(async context =>
        {
            var dropPath = Path.Combine(context.FilesDirectory, "drop");
            Directory.CreateDirectory(dropPath);
            await context.Store.AddReceiveLinkAsync(context.CreateReceiveLink("receive-token", dropPath), CancellationToken.None);
        });

        using var interruptedChunkForm = CreateChunkUploadForm(
            uploadId: "upload-1",
            relativePath: "large.bin",
            totalBytes: 10,
            chunkIndex: 0,
            chunkCount: 1,
            chunkStart: 0,
            chunkValue: "hello",
            declaredChunkSize: 10);
        using var interruptedResponse = await host.PublicClient.PostAsync("/r/receive-token", interruptedChunkForm);
        var interruptedBody = await interruptedResponse.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, interruptedResponse.StatusCode);
        Assert.Contains("The upload did not complete.", interruptedBody);
        Assert.True(File.Exists(Path.Combine(host.FilesDirectory, "drop", "large.bin.downloadpart")));

        using var interruptedTransfersResponse = await host.LocalClient.GetAsync("/api/transfers");
        var interruptedTransfers = await interruptedTransfersResponse.Content.ReadFromJsonAsync<List<TransferSnapshot>>(JsonOptions);
        var activeTransfer = Assert.Single(interruptedTransfers!);
        Assert.Equal(TransferState.InProgress, activeTransfer.State);
        Assert.Equal(5, activeTransfer.ProgressBytes);

        using var retryChunkForm = CreateChunkUploadForm(
            uploadId: "upload-1",
            relativePath: "large.bin",
            totalBytes: 10,
            chunkIndex: 0,
            chunkCount: 1,
            chunkStart: 0,
            chunkValue: "helloworld",
            declaredChunkSize: 10);
        using var retryResponse = await host.PublicClient.PostAsync("/r/receive-token", retryChunkForm);
        var retryBody = await retryResponse.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, retryResponse.StatusCode);
        Assert.Equal("helloworld", await File.ReadAllTextAsync(Path.Combine(host.FilesDirectory, "drop", "large.bin")));
        AssertNoPartialUploads(Path.Combine(host.FilesDirectory, "drop"));
        Assert.Contains("\"uploadedCount\":1", retryBody);

        using var completedTransfersResponse = await host.LocalClient.GetAsync("/api/transfers");
        var completedTransfers = await completedTransfersResponse.Content.ReadFromJsonAsync<List<TransferSnapshot>>(JsonOptions);
        var completedTransfer = Assert.Single(completedTransfers!);
        Assert.Equal(activeTransfer.Id, completedTransfer.Id);
        Assert.Equal(TransferState.Completed, completedTransfer.State);
        Assert.Equal(10, completedTransfer.ProgressBytes);
    }

    [Fact]
    public async Task ReceiveUpload_AssemblesBinaryChunkedFileAcrossRequests()
    {
        await using var host = await AgentTestHost.StartAsync(async context =>
        {
            var dropPath = Path.Combine(context.FilesDirectory, "drop");
            Directory.CreateDirectory(dropPath);
            await context.Store.AddReceiveLinkAsync(context.CreateReceiveLink("receive-token", dropPath), CancellationToken.None);
        });

        using var firstChunkRequest = CreateBinaryChunkUploadRequest(
            uploadId: "upload-1",
            relativePath: "folder/large.bin",
            totalBytes: 10,
            chunkIndex: 0,
            chunkCount: 2,
            chunkStart: 0,
            chunkValue: "hello");
        using var firstResponse = await host.PublicClient.SendAsync(firstChunkRequest);
        var firstBody = await firstResponse.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
        Assert.Contains("\"uploadedCount\":0", firstBody);
        Assert.False(File.Exists(Path.Combine(host.FilesDirectory, "drop", "folder", "large.bin")));
        Assert.True(File.Exists(Path.Combine(host.FilesDirectory, "drop", "folder", "large.bin.downloadpart")));

        using var firstTransfersResponse = await host.LocalClient.GetAsync("/api/transfers");
        var firstTransfers = await firstTransfersResponse.Content.ReadFromJsonAsync<List<TransferSnapshot>>(JsonOptions);
        var activeTransfer = Assert.Single(firstTransfers!);
        Assert.Equal(TransferState.InProgress, activeTransfer.State);
        Assert.Equal(5, activeTransfer.ProgressBytes);
        Assert.Equal(10, activeTransfer.ProgressTotalBytes);

        using var secondChunkRequest = CreateBinaryChunkUploadRequest(
            uploadId: "upload-1",
            relativePath: "folder/large.bin",
            totalBytes: 10,
            chunkIndex: 1,
            chunkCount: 2,
            chunkStart: 5,
            chunkValue: "world");
        using var secondResponse = await host.PublicClient.SendAsync(secondChunkRequest);
        var secondBody = await secondResponse.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, secondResponse.StatusCode);
        Assert.Equal("helloworld", await File.ReadAllTextAsync(Path.Combine(host.FilesDirectory, "drop", "folder", "large.bin")));
        AssertNoPartialUploads(Path.Combine(host.FilesDirectory, "drop"));
        Assert.Contains("\"uploadedCount\":1", secondBody);

        using var completedTransfersResponse = await host.LocalClient.GetAsync("/api/transfers");
        var completedTransfers = await completedTransfersResponse.Content.ReadFromJsonAsync<List<TransferSnapshot>>(JsonOptions);
        var completedTransfer = Assert.Single(completedTransfers!);
        Assert.Equal(activeTransfer.Id, completedTransfer.Id);
        Assert.Equal(TransferState.Completed, completedTransfer.State);
        Assert.Equal(10, completedTransfer.ProgressBytes);
        Assert.Equal(10, completedTransfer.ProgressTotalBytes);
    }

    [Fact]
    public async Task ReceiveUpload_DecodesCompressedMultipartChunk()
    {
        var chunkBytes = System.Text.Encoding.UTF8.GetBytes("helloworld");
        await using var host = await AgentTestHost.StartAsync(async context =>
        {
            var dropPath = Path.Combine(context.FilesDirectory, "drop");
            Directory.CreateDirectory(dropPath);
            await context.Store.AddReceiveLinkAsync(context.CreateReceiveLink("receive-token", dropPath), CancellationToken.None);
        });

        using var chunkForm = CreateChunkUploadForm(
            uploadId: "upload-1",
            relativePath: "compressed/multipart.txt",
            totalBytes: chunkBytes.Length,
            chunkIndex: 0,
            chunkCount: 1,
            chunkStart: 0,
            chunkValue: "helloworld",
            declaredChunkSize: chunkBytes.Length,
            chunkEncoding: "gzip",
            bodyBytes: CompressGzip(chunkBytes));
        using var response = await host.PublicClient.PostAsync("/r/receive-token", chunkForm);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("helloworld", await File.ReadAllTextAsync(Path.Combine(host.FilesDirectory, "drop", "compressed", "multipart.txt")));
        AssertNoPartialUploads(Path.Combine(host.FilesDirectory, "drop"));
        Assert.Contains("\"uploadedCount\":1", body);

        using var completedTransfersResponse = await host.LocalClient.GetAsync("/api/transfers");
        var completedTransfers = await completedTransfersResponse.Content.ReadFromJsonAsync<List<TransferSnapshot>>(JsonOptions);
        var completedTransfer = Assert.Single(completedTransfers!);
        Assert.Equal(TransferState.Completed, completedTransfer.State);
        Assert.Equal(chunkBytes.Length, completedTransfer.ProgressBytes);
        Assert.Equal(chunkBytes.Length, completedTransfer.ProgressTotalBytes);
    }

    [Fact]
    public async Task ReceiveUpload_DecodesCompressedBinaryChunk()
    {
        var chunkBytes = System.Text.Encoding.UTF8.GetBytes("helloworld");
        await using var host = await AgentTestHost.StartAsync(async context =>
        {
            var dropPath = Path.Combine(context.FilesDirectory, "drop");
            Directory.CreateDirectory(dropPath);
            await context.Store.AddReceiveLinkAsync(context.CreateReceiveLink("receive-token", dropPath), CancellationToken.None);
        });

        using var chunkRequest = CreateBinaryChunkUploadRequest(
            uploadId: "upload-1",
            relativePath: "compressed/binary.txt",
            totalBytes: chunkBytes.Length,
            chunkIndex: 0,
            chunkCount: 1,
            chunkStart: 0,
            chunkValue: "helloworld",
            declaredChunkSize: chunkBytes.Length,
            transferCompression: "gzip",
            bodyBytes: CompressGzip(chunkBytes));
        using var response = await host.PublicClient.SendAsync(chunkRequest);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("helloworld", await File.ReadAllTextAsync(Path.Combine(host.FilesDirectory, "drop", "compressed", "binary.txt")));
        AssertNoPartialUploads(Path.Combine(host.FilesDirectory, "drop"));
        Assert.Contains("\"uploadedCount\":1", body);
    }

    [Fact]
    public async Task ReceiveUpload_SavesCompressedStreamAndTracksDecodedProgress()
    {
        var contentBytes = System.Text.Encoding.UTF8.GetBytes(new string('a', 4096));
        await using var host = await AgentTestHost.StartAsync(async context =>
        {
            var dropPath = Path.Combine(context.FilesDirectory, "drop");
            Directory.CreateDirectory(dropPath);
            await context.Store.AddReceiveLinkAsync(context.CreateReceiveLink("receive-token", dropPath), CancellationToken.None);
        });

        using var request = CreateCompressedStreamUploadRequest(
            relativePath: "compressed/repeated.txt",
            expectedDecodedBytes: contentBytes.Length,
            contentBytes);
        var compressedWireBytes = request.Content!.Headers.ContentLength!.Value;
        using var response = await host.PublicClient.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        using var transfersResponse = await host.LocalClient.GetAsync("/api/transfers");
        var transfers = await transfersResponse.Content.ReadFromJsonAsync<List<TransferSnapshot>>(JsonOptions);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(contentBytes, await File.ReadAllBytesAsync(Path.Combine(host.FilesDirectory, "drop", "compressed", "repeated.txt")));
        AssertNoPartialUploads(Path.Combine(host.FilesDirectory, "drop"));
        Assert.Contains("\"uploadedCount\":1", body);
        var uploadTransfer = Assert.Single(transfers!);
        Assert.Equal(TransferState.Completed, uploadTransfer.State);
        Assert.Equal(compressedWireBytes, uploadTransfer.BytesSent);
        Assert.Equal(contentBytes.Length, uploadTransfer.TotalBytes);
        Assert.Equal(contentBytes.Length, uploadTransfer.ProgressBytes);
        Assert.Equal(contentBytes.Length, uploadTransfer.ProgressTotalBytes);
    }

    [Fact]
    public async Task ReceiveUpload_RejectsCompressedStreamWhenDecodedSizeDoesNotMatch()
    {
        var contentBytes = System.Text.Encoding.UTF8.GetBytes("short");
        await using var host = await AgentTestHost.StartAsync(async context =>
        {
            var dropPath = Path.Combine(context.FilesDirectory, "drop");
            Directory.CreateDirectory(dropPath);
            await context.Store.AddReceiveLinkAsync(context.CreateReceiveLink("receive-token", dropPath), CancellationToken.None);
        });

        using var request = CreateCompressedStreamUploadRequest(
            relativePath: "compressed/mismatch.txt",
            expectedDecodedBytes: contentBytes.Length + 1,
            contentBytes);
        using var response = await host.PublicClient.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        using var transfersResponse = await host.LocalClient.GetAsync("/api/transfers");
        var transfers = await transfersResponse.Content.ReadFromJsonAsync<List<TransferSnapshot>>(JsonOptions);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(File.Exists(Path.Combine(host.FilesDirectory, "drop", "compressed", "mismatch.txt")));
        AssertNoPartialUploads(Path.Combine(host.FilesDirectory, "drop"));
        Assert.Contains("\"uploadedCount\":0", body);
        Assert.Contains("The decoded upload size did not match the declared file size.", body);
        var uploadTransfer = Assert.Single(transfers!);
        Assert.Equal(TransferState.Failed, uploadTransfer.State);
        Assert.Equal(contentBytes.Length + 1, uploadTransfer.TotalBytes);
        Assert.Equal(contentBytes.Length, uploadTransfer.ProgressBytes);
        Assert.Equal(contentBytes.Length + 1, uploadTransfer.ProgressTotalBytes);
    }

    [Fact]
    public async Task ReceiveUpload_RejectsCompressedStreamWhenCompressedBodyExceedsConfiguredLimit()
    {
        var contentBytes = new byte[Defaults.MinimumReceiveUploadChunkSizeBytes + 1];
        new Random(42).NextBytes(contentBytes);

        await using var host = await AgentTestHost.StartAsync(async context =>
        {
            await context.Store.SaveSettingsAsync(
                context.Settings with { ReceiveUploadMaxBodySizeBytes = Defaults.MinimumReceiveUploadChunkSizeBytes },
                CancellationToken.None);
            var dropPath = Path.Combine(context.FilesDirectory, "drop");
            Directory.CreateDirectory(dropPath);
            await context.Store.AddReceiveLinkAsync(context.CreateReceiveLink("receive-token", dropPath), CancellationToken.None);
        });

        using var request = CreateCompressedStreamUploadRequest(
            relativePath: "compressed/too-large.gz",
            expectedDecodedBytes: contentBytes.Length,
            contentBytes);
        using var response = await host.PublicClient.SendAsync(request);
        using var transfersResponse = await host.LocalClient.GetAsync("/api/transfers");
        var transfers = await transfersResponse.Content.ReadFromJsonAsync<List<TransferSnapshot>>(JsonOptions);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.False(File.Exists(Path.Combine(host.FilesDirectory, "drop", "compressed", "too-large.gz")));
        AssertNoPartialUploads(Path.Combine(host.FilesDirectory, "drop"));
        Assert.Empty(transfers!);
    }

    [Fact]
    public async Task ReceiveUpload_RejectsCompressedStreamWhenDeclaredDecodedSizeExceedsQuota()
    {
        var contentBytes = System.Text.Encoding.UTF8.GetBytes("small");
        await using var host = await AgentTestHost.StartAsync(async context =>
        {
            var dropPath = Path.Combine(context.FilesDirectory, "drop");
            Directory.CreateDirectory(dropPath);
            await context.Store.AddReceiveLinkAsync(context.CreateReceiveLinkWithQuota("receive-token", dropPath, 8), CancellationToken.None);
        });

        using var request = CreateCompressedStreamUploadRequest(
            relativePath: "compressed/quota.txt",
            expectedDecodedBytes: 9,
            contentBytes);
        using var response = await host.PublicClient.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        using var transfersResponse = await host.LocalClient.GetAsync("/api/transfers");
        var transfers = await transfersResponse.Content.ReadFromJsonAsync<List<TransferSnapshot>>(JsonOptions);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Contains("\"uploadedCount\":0", body);
        Assert.Contains("This receive link has reached its upload limit.", body);
        Assert.False(File.Exists(Path.Combine(host.FilesDirectory, "drop", "compressed", "quota.txt")));
        AssertNoPartialUploads(Path.Combine(host.FilesDirectory, "drop"));
        Assert.Empty(transfers!);
    }

    [Fact]
    public async Task ReceiveUpload_AssemblesWebSocketChunkedFile()
    {
        await using var host = await AgentTestHost.StartAsync(async context =>
        {
            var dropPath = Path.Combine(context.FilesDirectory, "drop");
            Directory.CreateDirectory(dropPath);
            await context.Store.AddReceiveLinkAsync(context.CreateReceiveLink("receive-token", dropPath), CancellationToken.None);
        });

        using var socket = new ClientWebSocket();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{host.Settings.ManualPublicPort}/r/receive-token/upload-socket"), timeout.Token);

        var firstChunkBytes = System.Text.Encoding.UTF8.GetBytes("hello");
        await SendWebSocketUploadChunkAsync(
            socket,
            uploadId: "upload-1",
            relativePath: "socket/large.bin",
            totalBytes: 10,
            chunkIndex: 0,
            chunkCount: int.MaxValue,
            chunkStart: 0,
            chunkValue: "hello",
            timeout.Token,
            encoding: "gzip",
            bodyBytes: CompressGzip(firstChunkBytes),
            declaredChunkSize: firstChunkBytes.Length);
        using var firstAck = await ReceiveWebSocketJsonDocumentAsync(socket, timeout.Token);

        Assert.Equal(0, firstAck.RootElement.GetProperty("uploadedCount").GetInt32());
        Assert.False(File.Exists(Path.Combine(host.FilesDirectory, "drop", "socket", "large.bin")));
        Assert.True(File.Exists(Path.Combine(host.FilesDirectory, "drop", "socket", "large.bin.downloadpart")));

        await SendWebSocketUploadChunkAsync(
            socket,
            uploadId: "upload-1",
            relativePath: "socket/large.bin",
            totalBytes: 10,
            chunkIndex: 1,
            chunkCount: 2,
            chunkStart: 5,
            chunkValue: "world",
            timeout.Token);
        using var secondAck = await ReceiveWebSocketJsonDocumentAsync(socket, timeout.Token);

        Assert.Equal(1, secondAck.RootElement.GetProperty("uploadedCount").GetInt32());
        Assert.Equal("helloworld", await File.ReadAllTextAsync(Path.Combine(host.FilesDirectory, "drop", "socket", "large.bin")));
        AssertNoPartialUploads(Path.Combine(host.FilesDirectory, "drop"));
    }

    [Fact]
    public async Task ReceiveUpload_ReturnsConflictWhenChunkSessionIsMissing()
    {
        await using var host = await AgentTestHost.StartAsync(async context =>
        {
            var dropPath = Path.Combine(context.FilesDirectory, "drop");
            Directory.CreateDirectory(dropPath);
            await context.Store.AddReceiveLinkAsync(context.CreateReceiveLink("receive-token", dropPath), CancellationToken.None);
        });

        using var chunkRequest = CreateBinaryChunkUploadRequest(
            uploadId: "missing-session",
            relativePath: "folder/large.bin",
            totalBytes: 10,
            chunkIndex: 1,
            chunkCount: 2,
            chunkStart: 5,
            chunkValue: "world");
        using var response = await host.PublicClient.SendAsync(chunkRequest);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("The upload session was not found.", body);
        AssertNoPartialUploads(Path.Combine(host.FilesDirectory, "drop"));
    }

    [Fact]
    public async Task ReceiveUploadCancel_RollsBackChunkSessionAndPausesTransfer()
    {
        await using var host = await AgentTestHost.StartAsync(async context =>
        {
            var dropPath = Path.Combine(context.FilesDirectory, "drop");
            Directory.CreateDirectory(dropPath);
            await context.Store.AddReceiveLinkAsync(context.CreateReceiveLink("receive-token", dropPath), CancellationToken.None);
        });

        using var firstChunkRequest = CreateBinaryChunkUploadRequest(
            uploadId: "upload-cancel",
            relativePath: "folder/large.bin",
            totalBytes: 10,
            chunkIndex: 0,
            chunkCount: int.MaxValue,
            chunkStart: 0,
            chunkValue: "hello");
        using var firstResponse = await host.PublicClient.SendAsync(firstChunkRequest);

        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
        Assert.True(File.Exists(Path.Combine(host.FilesDirectory, "drop", "folder", "large.bin.downloadpart")));

        using var cancelResponse = await host.PublicClient.PostAsync("/r/receive-token/cancel-upload?uploadId=upload-cancel", null);
        using var transfersResponse = await host.LocalClient.GetAsync("/api/transfers");
        var transfers = await transfersResponse.Content.ReadFromJsonAsync<List<TransferSnapshot>>(JsonOptions);

        Assert.Equal(HttpStatusCode.OK, cancelResponse.StatusCode);
        Assert.False(File.Exists(Path.Combine(host.FilesDirectory, "drop", "folder", "large.bin")));
        AssertNoPartialUploads(Path.Combine(host.FilesDirectory, "drop"));
        var transfer = Assert.Single(transfers!);
        Assert.Equal(TransferState.Paused, transfer.State);
        Assert.False(transfer.IsActive);
        Assert.False(transfer.Succeeded);
        Assert.Equal("Upload stopped.", transfer.Error);
    }

    [Fact]
    public async Task ReceiveUploadEvents_StreamsHostProgressForReceiveLinkUploads()
    {
        const long firstProgressBytes = 1L * 1024 * 1024;
        const long secondProgressBytes = 10L * 1024 * 1024;
        const long totalBytes = 20L * 1024 * 1024;
        ReceiveLinkRecord? receiveLink = null;
        await using var host = await AgentTestHost.StartAsync(async context =>
        {
            var dropPath = Path.Combine(context.FilesDirectory, "drop");
            Directory.CreateDirectory(dropPath);
            await context.Store.SaveSettingsAsync(context.Settings with
            {
                ReceiveUploadChunkSizingMode = ReceiveUploadChunkSizingMode.Auto,
                ReceiveUploadChunkSizeBytes = Defaults.MinimumReceiveUploadChunkSizeBytes,
                ReceiveUploadMaxBodySizeBytes = 8L * 1024 * 1024,
            }, CancellationToken.None);
            receiveLink = context.CreateReceiveLink("receive-token", dropPath);
            await context.Store.AddReceiveLinkAsync(receiveLink, CancellationToken.None);
        });

        using var socket = new ClientWebSocket();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{host.Settings.ManualPublicPort}/r/receive-token/events"), timeout.Token);
        await SendUploadProgressSubscriptionAsync(socket, "upload-1", timeout.Token);

        var coordinator = host.App.Services.GetRequiredService<IShareCoordinator>();
        var transfer = await coordinator.StartTransferAsync(
            receiveLink!.Id,
            receiveLink.Token,
            "incoming.bin",
            TransferKind.FileUpload,
            clientSessionId: "upload-1",
            clientFingerprint: null,
            remoteAddress: "127.0.0.1",
            totalBytes: totalBytes,
            bytesSent: 0,
            requesterName: null,
            CancellationToken.None);

        using var startedDocument = await ReceiveUploadProgressDocumentAsync(socket, "upload-1", 0, timeout.Token);
        var startedRoot = startedDocument.RootElement;

        Assert.Equal("upload-1", startedRoot.GetProperty("uploadId").GetString());
        Assert.Equal(0, startedRoot.GetProperty("receivedBytes").GetInt64());
        var startedRecommendedChunkSize = startedRoot.GetProperty("recommendedChunkSizeBytes").GetInt64();
        Assert.InRange(startedRecommendedChunkSize, Defaults.MinimumReceiveUploadChunkSizeBytes, 8L * 1024 * 1024);

        await coordinator.UpdateTransferProgressAsync(
            transfer.Id,
            bytesSent: firstProgressBytes,
            CancellationToken.None,
            progressBytes: firstProgressBytes,
            progressTotalBytes: totalBytes);

        using var document = await ReceiveUploadProgressDocumentAsync(socket, "upload-1", firstProgressBytes, timeout.Token);
        var root = document.RootElement;

        Assert.Equal("upload-1", root.GetProperty("uploadId").GetString());
        Assert.Equal(firstProgressBytes, root.GetProperty("receivedBytes").GetInt64());
        Assert.Equal(totalBytes, root.GetProperty("totalBytes").GetInt64());
        Assert.Equal("InProgress", root.GetProperty("state").GetString());
        var firstRecommendedChunkSize = root.GetProperty("recommendedChunkSizeBytes").GetInt64();
        Assert.InRange(firstRecommendedChunkSize, startedRecommendedChunkSize, 8L * 1024 * 1024);

        await Task.Delay(TimeSpan.FromMilliseconds(50), timeout.Token);
        await coordinator.UpdateTransferProgressAsync(
            transfer.Id,
            bytesSent: secondProgressBytes,
            CancellationToken.None,
            progressBytes: secondProgressBytes,
            progressTotalBytes: totalBytes);

        using var secondDocument = await ReceiveUploadProgressDocumentAsync(socket, "upload-1", secondProgressBytes, timeout.Token);
        var secondRoot = secondDocument.RootElement;

        Assert.Equal(secondProgressBytes, secondRoot.GetProperty("receivedBytes").GetInt64());
        Assert.InRange(secondRoot.GetProperty("recommendedChunkSizeBytes").GetInt64(), firstRecommendedChunkSize, 8L * 1024 * 1024);
    }

    [Fact]
    public async Task ReceiveUploadEvents_DoesNotStreamProgressForUnsubscribedUploads()
    {
        const long progressBytes = 1L * 1024 * 1024;
        const long totalBytes = 2L * 1024 * 1024;
        ReceiveLinkRecord? receiveLink = null;
        await using var host = await AgentTestHost.StartAsync(async context =>
        {
            var dropPath = Path.Combine(context.FilesDirectory, "drop");
            Directory.CreateDirectory(dropPath);
            receiveLink = context.CreateReceiveLink("receive-token", dropPath);
            await context.Store.AddReceiveLinkAsync(receiveLink, CancellationToken.None);
        });

        using var subscribedSocket = new ClientWebSocket();
        using var otherSocket = new ClientWebSocket();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var eventsUri = new Uri($"ws://127.0.0.1:{host.Settings.ManualPublicPort}/r/receive-token/events");
        await subscribedSocket.ConnectAsync(eventsUri, timeout.Token);
        await otherSocket.ConnectAsync(eventsUri, timeout.Token);
        await SendUploadProgressSubscriptionAsync(subscribedSocket, "upload-1", timeout.Token);
        await SendUploadProgressSubscriptionAsync(otherSocket, "upload-2", timeout.Token);

        await Task.Delay(TimeSpan.FromMilliseconds(50), timeout.Token);

        var coordinator = host.App.Services.GetRequiredService<IShareCoordinator>();
        var transfer = await coordinator.StartTransferAsync(
            receiveLink!.Id,
            receiveLink.Token,
            "incoming.bin",
            TransferKind.FileUpload,
            clientSessionId: "upload-1",
            clientFingerprint: null,
            remoteAddress: "127.0.0.1",
            totalBytes: totalBytes,
            bytesSent: 0,
            requesterName: null,
            CancellationToken.None);

        await coordinator.UpdateTransferProgressAsync(
            transfer.Id,
            bytesSent: progressBytes,
            CancellationToken.None,
            progressBytes: progressBytes,
            progressTotalBytes: totalBytes);

        using var document = await ReceiveUploadProgressDocumentAsync(subscribedSocket, "upload-1", progressBytes, timeout.Token);
        Assert.Equal("upload-1", document.RootElement.GetProperty("uploadId").GetString());

        await AssertNoWebSocketMessageAsync(otherSocket, TimeSpan.FromMilliseconds(250));
    }

    [Fact]
    public async Task ReceiveUpload_AcceptsLargeMultipartBody()
    {
        const int largeByteCount = 2 * 1024 * 1024;
        await using var host = await AgentTestHost.StartAsync(async context =>
        {
            var dropPath = Path.Combine(context.FilesDirectory, "drop");
            Directory.CreateDirectory(dropPath);
            await context.Store.AddReceiveLinkAsync(context.CreateReceiveLink("receive-token", dropPath), CancellationToken.None);
        });

        using var form = new MultipartFormDataContent();
        form.Add(new StringContent("large.bin"), "relativePaths");
        form.Add(new StringContent(largeByteCount.ToString()), "fileSizes");
        form.Add(CreateFileContent(largeByteCount), "files", "large.bin");

        host.PublicClient.Timeout = TimeSpan.FromMinutes(5);
        using var response = await host.PublicClient.PostAsync("/r/receive-token", form);
        var body = await response.Content.ReadAsStringAsync();
        using var transfersResponse = await host.LocalClient.GetAsync("/api/transfers");
        var transfers = await transfersResponse.Content.ReadFromJsonAsync<List<TransferSnapshot>>(JsonOptions);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(largeByteCount, new FileInfo(Path.Combine(host.FilesDirectory, "drop", "large.bin")).Length);
        Assert.Contains("\"uploadedCount\":1", body);
        AssertNoPartialUploads(Path.Combine(host.FilesDirectory, "drop"));
        var uploadTransfer = Assert.Single(transfers!);
        Assert.Equal(largeByteCount, uploadTransfer.TotalBytes);
        Assert.Equal(largeByteCount, uploadTransfer.ProgressTotalBytes);
        Assert.Equal(largeByteCount, uploadTransfer.ProgressBytes);
    }

    [Fact]
    public async Task ControlApi_DeleteTransfer_RemovesSpecificHistoryEntry()
    {
        await using var host = await AgentTestHost.StartAsync(async context =>
        {
            await context.Store.SaveTransferAsync(new TransferSnapshot
            {
                Id = "transfer-1",
                ShareId = "share-1",
                Token = "token-1",
                FileName = "report.pdf",
                TransferKind = TransferKind.FileDownload,
                BytesSent = 128,
                TotalBytes = 128,
                StartedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-2),
                LastUpdatedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1),
                CompletedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1),
                State = TransferState.Completed,
                IsActive = false,
                Succeeded = true,
            }, CancellationToken.None);
            await context.Store.SaveTransferAsync(new TransferSnapshot
            {
                Id = "transfer-2",
                ShareId = "share-2",
                Token = "token-2",
                FileName = "other.pdf",
                TransferKind = TransferKind.FileDownload,
                BytesSent = 64,
                TotalBytes = 128,
                StartedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1),
                LastUpdatedAtUtc = DateTimeOffset.UtcNow,
                CompletedAtUtc = DateTimeOffset.UtcNow,
                State = TransferState.Completed,
                IsActive = false,
                Succeeded = true,
            }, CancellationToken.None);
        });

        using var deleteResponse = await host.LocalClient.DeleteAsync("/api/transfers/transfer-1");
        using var listResponse = await host.LocalClient.GetAsync("/api/transfers");
        var transfers = await listResponse.Content.ReadFromJsonAsync<List<TransferSnapshot>>(JsonOptions);

        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);
        Assert.NotNull(transfers);
        Assert.Single(transfers!);
        Assert.Equal("transfer-2", transfers[0].Id);
    }

    [Fact]
    public async Task ControlApi_ClearTransfers_RemovesCompletedHistoryAndKeepsActiveTransfers()
    {
        await using var host = await AgentTestHost.StartAsync(async context =>
        {
            await context.Store.SaveTransferAsync(new TransferSnapshot
            {
                Id = "transfer-completed",
                ShareId = "share-1",
                Token = "token-1",
                FileName = "report.pdf",
                TransferKind = TransferKind.FileDownload,
                BytesSent = 128,
                TotalBytes = 128,
                StartedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-2),
                LastUpdatedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1),
                CompletedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1),
                State = TransferState.Completed,
                IsActive = false,
                Succeeded = true,
            }, CancellationToken.None);
        });

        var coordinator = host.App.Services.GetRequiredService<IShareCoordinator>();
        var activeTransfer = await coordinator.StartTransferAsync(
            "share-2",
            "token-2",
            "active.pdf",
            TransferKind.FileDownload,
            clientSessionId: "session-2",
            clientFingerprint: "fingerprint-2",
            remoteAddress: "127.0.0.1",
            totalBytes: 128,
            bytesSent: 64,
            requesterName: null,
            CancellationToken.None);

        using var deleteResponse = await host.LocalClient.DeleteAsync("/api/transfers");
        using var listResponse = await host.LocalClient.GetAsync("/api/transfers");
        var transfers = await listResponse.Content.ReadFromJsonAsync<List<TransferSnapshot>>(JsonOptions);

        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);
        Assert.NotNull(transfers);
        Assert.Single(transfers!);
        Assert.Equal(activeTransfer.Id, transfers[0].Id);
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    private static string ComputeSha256Hex(byte[] bytes, int offset, int count)
    {
        return Convert.ToHexString(SHA256.HashData(bytes.AsSpan(offset, count))).ToLowerInvariant();
    }

    private static ByteArrayContent CreateFileContent(string value)
    {
        return new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes(value));
    }

    private static StreamContent CreateFileContent(long byteCount)
    {
        return new StreamContent(new RepeatingByteStream(byteCount, (byte)'x'));
    }

    private static MultipartFormDataContent CreateChunkUploadForm(
        string uploadId,
        string relativePath,
        long totalBytes,
        int chunkIndex,
        int chunkCount,
        long chunkStart,
        string chunkValue,
        long? declaredChunkSize = null,
        string? chunkEncoding = null,
        byte[]? bodyBytes = null)
    {
        var form = new MultipartFormDataContent();
        form.Add(new StringContent(relativePath), "relativePaths");
        form.Add(new StringContent(totalBytes.ToString()), "fileSizes");
        form.Add(new StringContent(uploadId), "uploadId");
        form.Add(new StringContent(chunkIndex.ToString()), "chunkIndex");
        form.Add(new StringContent(chunkCount.ToString()), "chunkCount");
        form.Add(new StringContent(chunkStart.ToString()), "chunkStart");
        form.Add(new StringContent((declaredChunkSize ?? System.Text.Encoding.UTF8.GetByteCount(chunkValue)).ToString()), "chunkSize");
        if (!string.IsNullOrWhiteSpace(chunkEncoding))
        {
            form.Add(new StringContent(chunkEncoding), "chunkEncoding");
        }

        form.Add(bodyBytes is null ? CreateFileContent(chunkValue) : new ByteArrayContent(bodyBytes), "files", Path.GetFileName(relativePath));
        return form;
    }

    private static HttpRequestMessage CreateBinaryChunkUploadRequest(
        string uploadId,
        string relativePath,
        long totalBytes,
        int chunkIndex,
        int chunkCount,
        long chunkStart,
        string chunkValue,
        long? declaredChunkSize = null,
        string? transferCompression = null,
        byte[]? bodyBytes = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/r/receive-token")
        {
            Content = bodyBytes is null ? CreateFileContent(chunkValue) : new ByteArrayContent(bodyBytes),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        request.Headers.Add("X-IFS-Upload-Id", uploadId);
        request.Headers.Add("X-IFS-Batch-Id", "batch-1");
        request.Headers.Add("X-IFS-Relative-Path", Uri.EscapeDataString(relativePath));
        request.Headers.Add("X-IFS-File-Name", Uri.EscapeDataString(Path.GetFileName(relativePath)));
        request.Headers.Add("X-IFS-File-Size", totalBytes.ToString());
        request.Headers.Add("X-IFS-Chunk-Index", chunkIndex.ToString());
        request.Headers.Add("X-IFS-Chunk-Count", chunkCount.ToString());
        request.Headers.Add("X-IFS-Chunk-Start", chunkStart.ToString());
        request.Headers.Add("X-IFS-Chunk-Size", (declaredChunkSize ?? System.Text.Encoding.UTF8.GetByteCount(chunkValue)).ToString());
        if (!string.IsNullOrWhiteSpace(transferCompression))
        {
            request.Headers.Add("X-IFS-Transfer-Compression", transferCompression);
        }

        return request;
    }

    private static HttpRequestMessage CreateCompressedStreamUploadRequest(
        string relativePath,
        long expectedDecodedBytes,
        byte[] contentBytes)
    {
        var compressedBytes = CompressGzip(contentBytes);
        var request = new HttpRequestMessage(HttpMethod.Post, "/r/receive-token")
        {
            Content = new ByteArrayContent(compressedBytes),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/gzip");
        request.Content.Headers.ContentLength = compressedBytes.Length;
        request.Headers.Add("X-IFS-Upload-Id", "compressed-upload-1");
        request.Headers.Add("X-IFS-Batch-Id", "batch-1");
        request.Headers.Add("X-IFS-Relative-Path", Uri.EscapeDataString(relativePath));
        request.Headers.Add("X-IFS-File-Name", Uri.EscapeDataString(Path.GetFileName(relativePath)));
        request.Headers.Add("X-IFS-File-Size", expectedDecodedBytes.ToString());
        return request;
    }

    private static byte[] CompressGzip(byte[] contentBytes)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            gzip.Write(contentBytes);
        }

        return output.ToArray();
    }

    private static async Task SendWebSocketUploadChunkAsync(
        ClientWebSocket socket,
        string uploadId,
        string relativePath,
        long totalBytes,
        int chunkIndex,
        int chunkCount,
        long chunkStart,
        string chunkValue,
        CancellationToken cancellationToken,
        string? encoding = null,
        byte[]? bodyBytes = null,
        long? declaredChunkSize = null)
    {
        var chunkBytes = System.Text.Encoding.UTF8.GetBytes(chunkValue);
        var metadata = JsonSerializer.Serialize(new
        {
            type = "chunk",
            uploadId,
            batchId = "batch-1",
            relativePath,
            fileName = Path.GetFileName(relativePath),
            fileSize = totalBytes,
            chunkIndex,
            chunkCount,
            chunkStart,
            chunkSize = declaredChunkSize ?? chunkBytes.Length,
            encoding = encoding ?? "raw",
        }, JsonOptions);
        await socket.SendAsync(System.Text.Encoding.UTF8.GetBytes(metadata), WebSocketMessageType.Text, true, cancellationToken);
        await socket.SendAsync(bodyBytes ?? chunkBytes, WebSocketMessageType.Binary, true, cancellationToken);
    }

    private static async Task<JsonDocument> ReceiveWebSocketJsonDocumentAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        using var stream = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                throw new InvalidOperationException("The websocket closed before the expected response arrived.");
            }

            stream.Write(buffer, 0, result.Count);
        }
        while (!result.EndOfMessage);

        stream.Position = 0;
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
    }

    private static async Task<JsonDocument> ReceiveUploadProgressDocumentAsync(
        ClientWebSocket socket,
        string uploadId,
        long receivedBytes,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        while (true)
        {
            using var stream = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(buffer, cancellationToken);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    throw new InvalidOperationException("The upload events websocket closed before the expected event arrived.");
                }

                stream.Write(buffer, 0, result.Count);
            }
            while (!result.EndOfMessage);

            stream.Position = 0;
            var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            if (document.RootElement.TryGetProperty("uploadId", out var uploadIdProperty) &&
                string.Equals(uploadIdProperty.GetString(), uploadId, StringComparison.Ordinal) &&
                document.RootElement.TryGetProperty("receivedBytes", out var receivedBytesProperty) &&
                receivedBytesProperty.GetInt64() == receivedBytes)
            {
                return document;
            }

            document.Dispose();
        }
    }

    private static Task SendUploadProgressSubscriptionAsync(
        ClientWebSocket socket,
        string uploadId,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            type = "subscribe",
            uploadIds = new[] { uploadId },
        }, JsonOptions);
        return socket.SendAsync(payload, WebSocketMessageType.Text, true, cancellationToken);
    }

    private static async Task AssertNoWebSocketMessageAsync(ClientWebSocket socket, TimeSpan waitTime)
    {
        using var timeout = new CancellationTokenSource(waitTime);
        var buffer = new byte[4096];
        var receivedMessage = false;

        try
        {
            await socket.ReceiveAsync(buffer, timeout.Token);
            receivedMessage = true;
        }
        catch (OperationCanceledException)
        {
        }

        Assert.False(receivedMessage, "The websocket received an upload progress event for an unsubscribed upload ID.");
    }

    private static void AssertNoPartialUploads(string directoryPath)
    {
        Assert.Empty(Directory.GetFiles(directoryPath, "*.downloadpart", SearchOption.AllDirectories));
    }

    private sealed class RepeatingByteStream(long length, byte value) : Stream
    {
        private long _position;

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => length;

        public override long Position
        {
            get => _position;
            set => _position = Math.Clamp(value, 0, length);
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_position >= length)
            {
                return 0;
            }

            var bytesToRead = (int)Math.Min(count, length - _position);
            Array.Fill(buffer, value, offset, bytesToRead);
            _position += bytesToRead;
            return bytesToRead;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            Position = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => _position + offset,
                SeekOrigin.End => length + offset,
                _ => _position,
            };
            return _position;
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }
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
                    RepositoryRoot = AgentPaths.GetRepositoryRoot(),
                    PublicShareAssetsDirectory = assetsDirectory,
                    BootstrapSettingsPath = Path.Combine(rootPath, "bootstrap-settings.json"),
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
        public ReceiveLinkRecord CreateReceiveLink(string token, string folderPath)
            => CreateReceiveLinkWithQuota(token, folderPath, Defaults.DefaultReceiveMaxTotalBytes);

        public ReceiveLinkRecord CreateReceiveLinkWithQuota(string token, string folderPath, long maxTotalBytes)
        {
            var directoryInfo = new DirectoryInfo(folderPath);
            return new ReceiveLinkRecord
            {
                Id = Guid.NewGuid().ToString("N"),
                Token = token,
                TargetDirectoryPath = directoryInfo.FullName,
                TargetDisplayName = directoryInfo.Name,
                PublicBaseUrl = Settings.ManualBaseUrl ?? $"http://127.0.0.1:{Settings.ManualPublicPort}",
                CreatedAtUtc = DateTimeOffset.UtcNow,
                ExpiresAtUtc = DateTimeOffset.UtcNow.AddDays(1),
                MaxTotalBytes = maxTotalBytes,
                BytesReceived = 0,
                PublishMode = PublishMode.Manual,
                State = ReceiveLinkState.Active,
            };
        }

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
