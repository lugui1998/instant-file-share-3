using System.Net;
using InstantFileShare.Agent;
using InstantFileShare.Core;
using Microsoft.AspNetCore.Http;

namespace InstantFileShare.Agent.Tests;

public sealed class PublicShareHelpersTests
{
    [Fact]
    public void ResolveClientIpAddress_PrefersForwardedHeadersWhenRemoteIsLoopback()
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Loopback;
        context.Request.Headers["CF-Connecting-IP"] = "203.0.113.10";

        var resolvedAddress = RequestAddressResolver.ResolveClientIpAddress(context);

        Assert.Equal("203.0.113.10", resolvedAddress);
    }

    [Fact]
    public void BuildClientFingerprint_ReturnsStableHash()
    {
        var left = RequestAddressResolver.BuildClientFingerprint("203.0.113.10", "UnitTestAgent/1.0");
        var right = RequestAddressResolver.BuildClientFingerprint("203.0.113.10", "UnitTestAgent/1.0");

        Assert.NotNull(left);
        Assert.Equal(left, right);
        Assert.Equal(64, left!.Length);
    }

    [Fact]
    public void ResolveDownloadSession_UsesExistingCookie()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.Cookie = "ifs-download-token123=session-abc";

        var result = DownloadSessionManager.ResolveDownloadSession(context, "token123");

        Assert.Equal("session-abc", result.SessionId);
        Assert.False(result.SetCookie);
    }

    [Fact]
    public void BuildContentDispositionHeader_EncodesUtf8Filename()
    {
        var header = ResponseHeaderWriter.BuildContentDispositionHeader("attachment", "Quarterly Report ü.pdf");

        Assert.Contains("attachment;", header);
        Assert.Contains("filename=", header);
        Assert.Contains("filename*=UTF-8''Quarterly%20Report%20%C3%BC.pdf", header);
    }

    [Fact]
    public void PublicShareAssetLocator_LoadsManifestEntry()
    {
        using var tempDirectory = new TemporaryDirectory();
        var assetRoot = tempDirectory.CreateDirectory("public-share");
        Directory.CreateDirectory(Path.Combine(assetRoot, ".vite"));
        Directory.CreateDirectory(Path.Combine(assetRoot, "assets"));
        File.WriteAllText(
            Path.Combine(assetRoot, ".vite", "manifest.json"),
            """
            {
              "src/public-share/main.ts": {
                "file": "assets/public-share.js",
                "isEntry": true,
                "css": ["assets/public-share.css"]
              }
            }
            """);
        File.WriteAllText(Path.Combine(assetRoot, "assets", "public-share.js"), "console.log('ok');");
        File.WriteAllText(Path.Combine(assetRoot, "assets", "public-share.css"), "body{}");

        var locator = new PublicShareAssetLocator(new AgentApplicationOptions
        {
            PublicShareAssetsDirectory = assetRoot,
            RepositoryRoot = tempDirectory.RootPath,
        });

        var found = locator.TryGetShellAssets(out var shellAssets, out var error);

        Assert.True(found, error);
        Assert.Equal("/public-share-assets/assets/public-share.js", shellAssets.EntryScriptUrl);
        Assert.Single(shellAssets.StylesheetUrls);
        Assert.Equal("/public-share-assets/assets/public-share.css", shellAssets.StylesheetUrls[0]);
    }

    [Theory]
    [InlineData("../secret.txt")]
    [InlineData("..\\secret.txt")]
    [InlineData("docs/../../secret.txt")]
    [InlineData("docs\\..\\..\\secret.txt")]
    [InlineData("..%2fsecret.txt")]
    [InlineData("..%5csecret.txt")]
    [InlineData("./inside.txt")]
    [InlineData(".\\inside.txt")]
    [InlineData("docs/./guide.txt")]
    [InlineData("docs\\.\\guide.txt")]
    [InlineData("....//secret.txt")]
    [InlineData("....\\\\secret.txt")]
    [InlineData(".. /.. /Windows/System32/config/SAM")]
    [InlineData(".. .\\.. .\\Windows\\System32\\config\\SAM")]
    [InlineData("C:\\Windows\\notepad.exe")]
    [InlineData("\\\\server\\share\\file.txt")]
    public void TryResolveEntry_RejectsTraversalAndAbsolutePaths(string candidatePath)
    {
        using var tempDirectory = new TemporaryDirectory();
        var rootPath = tempDirectory.CreateDirectory("share-root");
        File.WriteAllText(Path.Combine(rootPath, "inside.txt"), "inside");
        File.WriteAllText(Path.Combine(tempDirectory.RootPath, "secret.txt"), "secret");

        var resolved = FolderSharePathResolver.TryResolveEntry(rootPath, candidatePath, out var entry);

        Assert.False(resolved);
        Assert.Null(entry);
    }

    [Theory]
    [InlineData(".. /inside.txt")]
    [InlineData("folder./inside.txt")]
    [InlineData("folder /inside.txt")]
    public void TryResolveEntry_OnWindows_RejectsSegmentsWithTrailingDotOrSpace(string candidatePath)
    {
        using var tempDirectory = new TemporaryDirectory();
        var rootPath = tempDirectory.CreateDirectory("share-root");
        File.WriteAllText(Path.Combine(rootPath, "inside.txt"), "inside");

        var resolved = FolderSharePathResolver.TryResolveEntry(rootPath, candidatePath, out var entry);

        Assert.False(resolved);
        Assert.Null(entry);
    }

    [Fact]
    public void BuildFolderBrowsePage_CreatesBreadcrumbsAndEntries()
    {
        using var tempDirectory = new TemporaryDirectory();
        var rootPath = tempDirectory.CreateDirectory("share-root");
        Directory.CreateDirectory(Path.Combine(rootPath, "docs"));
        File.WriteAllText(Path.Combine(rootPath, "docs", "guide.txt"), "hello");

        Assert.True(FolderSharePathResolver.TryResolveEntry(rootPath, "docs", out var resolvedEntry));
        Assert.NotNull(resolvedEntry);

        var entries = FolderSharePathResolver.ListDirectory(resolvedEntry!);
        var context = new DefaultHttpContext();
        context.Request.Scheme = "http";
        context.Request.Host = new HostString("127.0.0.1:46431");
        context.Request.Path = "/s/folder-token/team-files/docs";

        var share = new ShareRecord
        {
            Id = "share-folder",
            Token = "folder-token",
            FilePath = rootPath,
            FileName = "Team Files",
            Slug = "team-files",
            PublicBaseUrl = "http://127.0.0.1:46431",
            FileSize = 0,
            FileModifiedAtUtc = DateTimeOffset.UtcNow,
            ShareKind = ShareKind.Folder,
            CanBrowseFolderContents = true,
            CanDownloadFolderAsZip = true,
            PrimaryFolderEntryPoint = FolderShareEntryPoint.Browse,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            PublishMode = PublishMode.Manual,
            State = ShareState.Active,
        };

        var page = new PublicSharePageModelFactory().BuildFolderBrowsePage(context, share, resolvedEntry!, entries);

        Assert.Equal("folder", page.Kind);
        Assert.NotNull(page.Folder);
        Assert.Equal(2, page.Folder!.Breadcrumbs.Count);
        Assert.Contains(page.Folder.Entries, entry => entry.IsParentDirectory);
        Assert.Contains(page.Folder.Entries, entry => entry.Name == "guide.txt");
        Assert.Equal("/s/folder-token/team-files/docs/guide.txt", page.Folder.Entries.Single(entry => entry.Name == "guide.txt").Href);
    }

    [Fact]
    public void BuildFileAndZipPages_CreatesExpectedModels()
    {
        using var tempDirectory = new TemporaryDirectory();
        var filePath = Path.Combine(tempDirectory.RootPath, "report.pdf");
        File.WriteAllText(filePath, "pdf");
        var fileInfo = new FileInfo(filePath);

        var context = new DefaultHttpContext();
        context.Request.Scheme = "http";
        context.Request.Host = new HostString("127.0.0.1:46431");
        context.Request.Path = "/s/file-token";

        var share = new ShareRecord
        {
            Id = "share-file",
            Token = "file-token",
            FilePath = filePath,
            FileName = "report.pdf",
            Slug = "report.pdf",
            PublicBaseUrl = "http://127.0.0.1:46431",
            FileSize = fileInfo.Length,
            FileModifiedAtUtc = new DateTimeOffset(fileInfo.LastWriteTimeUtc),
            CreatedAtUtc = DateTimeOffset.UtcNow,
            PublishMode = PublishMode.Manual,
            State = ShareState.Active,
        };

        var factory = new PublicSharePageModelFactory();
        var filePage = factory.BuildFileMetadataPage(context, share, fileInfo.Name, fileInfo, ShareFileResponsePolicy.Resolve(fileInfo.Name));
        Assert.True(FolderSharePathResolver.TryResolveEntry(tempDirectory.RootPath, null, out var resolvedRoot));
        Assert.NotNull(resolvedRoot);
        var zipPage = factory.BuildFolderZipMetadataPage(context, share with { ShareKind = ShareKind.Folder, FilePath = tempDirectory.RootPath }, resolvedRoot!);

        Assert.Equal("file", filePage.Kind);
        Assert.NotNull(filePage.File);
        Assert.Equal("zip", zipPage.Kind);
        Assert.NotNull(zipPage.Zip);
    }

    [Fact]
    public void BuildReceivePage_CreatesExpectedModel()
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("share.example.test");
        context.Request.Path = "/r/receive-token";

        var receiveLink = new ReceiveLinkRecord
        {
            Id = "receive-1",
            Token = "receive-token",
            TargetDirectoryPath = @"C:\Uploads\drop",
            TargetDisplayName = "drop",
            PublicBaseUrl = "https://share.example.test",
            CreatedAtUtc = DateTimeOffset.UtcNow,
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(6),
            MaxTotalBytes = 1024,
            BytesReceived = 256,
            PublishMode = PublishMode.Manual,
            State = ReceiveLinkState.Active,
        };

        var page = new PublicSharePageModelFactory().BuildReceivePage(context, receiveLink);

        Assert.Equal("receive", page.Kind);
        Assert.NotNull(page.Receive);
        Assert.Equal("drop", page.Receive!.TargetName);
        Assert.Equal("https://share.example.test/r/receive-token", page.Receive.UploadUrl);
        Assert.Equal(768, page.Receive.RemainingQuotaBytes);
    }

    [Fact]
    public void ReceiveUploadPlanner_RenamesConflictingRootDirectoryOnce()
    {
        using var tempDirectory = new TemporaryDirectory();
        var targetPath = tempDirectory.CreateDirectory("drop");
        Directory.CreateDirectory(Path.Combine(targetPath, "Photos"));

        var first = CreateFormFile("a.txt", 1);
        var second = CreateFormFile("b.txt", 1);
        var (planned, rejected) = ReceiveUploadPlanner.Plan(
            targetPath,
            [
                new ReceiveUploadCandidate(first, "Photos/2026/a.txt"),
                new ReceiveUploadCandidate(second, "Photos/2026/b.txt"),
            ]);

        Assert.Empty(rejected);
        Assert.Equal(2, planned.Count);
        Assert.All(planned, entry => Assert.StartsWith("Photos (1)/2026/", entry.StoredRelativePath, StringComparison.Ordinal));
    }

    [Fact]
    public void ReceiveUploadPlanner_RejectsTraversalPath()
    {
        using var tempDirectory = new TemporaryDirectory();
        var targetPath = tempDirectory.CreateDirectory("drop");
        var formFile = CreateFormFile("evil.txt", 1);

        var (_, rejected) = ReceiveUploadPlanner.Plan(
            targetPath,
            [new ReceiveUploadCandidate(formFile, "../evil.txt")]);

        Assert.Single(rejected);
        Assert.False(rejected[0].Success);
    }

    [Fact]
    public void ReceiveUploadPlanner_RejectsPathsThroughSymlinkedDirectory()
    {
        using var tempDirectory = new TemporaryDirectory();
        var targetPath = tempDirectory.CreateDirectory("drop");
        var outsidePath = tempDirectory.CreateDirectory("outside");
        var linkedDirectoryPath = Path.Combine(targetPath, "linked");

        if (!TryCreateDirectorySymbolicLink(linkedDirectoryPath, outsidePath))
        {
            return;
        }

        var formFile = CreateFormFile("safe.txt", 1);
        var (_, rejected) = ReceiveUploadPlanner.Plan(
            targetPath,
            [new ReceiveUploadCandidate(formFile, "linked/safe.txt")]);

        Assert.Single(rejected);
        Assert.False(rejected[0].Success);
        Assert.Equal("The uploaded path traverses a linked folder, which is not allowed.", rejected[0].Message);
    }

    private static IFormFile CreateFormFile(string fileName, int byteCount)
    {
        var content = new byte[byteCount];
        var stream = new MemoryStream(content);
        return new FormFile(stream, 0, byteCount, "files", fileName);
    }

    private static bool TryCreateDirectorySymbolicLink(string linkPath, string targetPath)
    {
        try
        {
            Directory.CreateSymbolicLink(linkPath, targetPath);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (PlatformNotSupportedException)
        {
            return false;
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            RootPath = Path.Combine(Path.GetTempPath(), "ifs-agent-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(RootPath);
        }

        public string RootPath { get; }

        public string CreateDirectory(string name)
        {
            var fullPath = Path.Combine(RootPath, name);
            Directory.CreateDirectory(fullPath);
            return fullPath;
        }

        public void Dispose()
        {
            DeleteDirectoryEventually(RootPath);
        }

        private static void DeleteDirectoryEventually(string path)
        {
            if (!Directory.Exists(path))
            {
                return;
            }

            for (var attempt = 1; attempt <= 5; attempt++)
            {
                try
                {
                    Directory.Delete(path, recursive: true);
                    return;
                }
                catch (IOException) when (attempt < 5)
                {
                    Thread.Sleep(50);
                }
                catch (UnauthorizedAccessException) when (attempt < 5)
                {
                    Thread.Sleep(50);
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

            // Temp cleanup should not fail the test when Windows still holds a short-lived handle.
        }
    }
}
