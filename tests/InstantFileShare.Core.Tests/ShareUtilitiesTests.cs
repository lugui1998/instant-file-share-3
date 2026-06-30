using InstantFileShare.Core;

namespace InstantFileShare.Core.Tests;

public sealed class ShareUtilitiesTests
{
    [Theory]
    [InlineData(6)]
    [InlineData(11)]
    [InlineData(22)]
    public void Generate_ShouldProduceBase62TokenWithRequestedLength(int length)
    {
        var token = ShareTokenGenerator.Generate(length);

        Assert.Equal(length, token.Length);
        Assert.Matches("^[0-9A-Za-z]+$", token);
    }

    [Fact]
    public void CreateSlug_ShouldNormalizeReadableSlug()
    {
        var slug = FileNameSlug.Create("Quarterly Report (Final) 2026.pdf");

        Assert.Equal("quarterly-report-final-2026", slug);
    }

    [Fact]
    public void CreateSlug_ShouldKeepFolderDotsWhenExtensionStrippingDisabled()
    {
        var slug = FileNameSlug.Create("Quarterly.Report.Folder", stripExtension: false);

        Assert.Equal("quarterly-report-folder", slug);
    }

    [Fact]
    public void Build_ShouldAppendSlugWhenPresent()
    {
        var url = ShareUrlBuilder.Build("https://example.com/", "abc123", "report");

        Assert.Equal("https://example.com/s/abc123/report", url);
    }

    [Fact]
    public void Build_ShouldAppendFileExtensionToFriendlySlug()
    {
        var url = ShareUrlBuilder.Build("https://example.com/", "abc123", "quarterly-report", "Quarterly Report.pdf");

        Assert.Equal("https://example.com/s/abc123/quarterly-report.pdf", url);
    }

    [Fact]
    public void Build_ShouldNotDuplicateExistingFileExtension()
    {
        var url = ShareUrlBuilder.Build("https://example.com/", "abc123", "quarterly-report.pdf", "Quarterly Report.pdf");

        Assert.Equal("https://example.com/s/abc123/quarterly-report.pdf", url);
    }

    [Fact]
    public void BuildFolderBrowse_ShouldUseTokenAsFolderRoot()
    {
        var url = ShareUrlBuilder.BuildFolderBrowse("https://example.com/", "abc123", "team-photos");

        Assert.Equal("https://example.com/s/abc123", url);
    }

    [Fact]
    public void BuildFolderZip_ShouldUseDownloadQueryOnFolderRoot()
    {
        var url = ShareUrlBuilder.BuildFolderZip("https://example.com/", "abc123", "team-photos");

        Assert.Equal("https://example.com/s/abc123?download=zip", url);
    }

    [Fact]
    public void BuildFolderChild_ShouldAppendOnlyRelativePath()
    {
        var url = ShareUrlBuilder.BuildFolderChild("https://example.com/", "abc123", "team-photos", "docs/Guide.pdf");

        Assert.Equal("https://example.com/s/abc123/docs/Guide.pdf", url);
    }

    [Theory]
    [InlineData("photo.JPG", "image/jpeg", ShareFileTypeCategory.Image, true)]
    [InlineData("clip.mp4", "video/mp4", ShareFileTypeCategory.Video, true)]
    [InlineData("report.pdf", "application/pdf", ShareFileTypeCategory.Pdf, true)]
    [InlineData("archive.zip", ShareFileResponsePolicy.DefaultContentType, ShareFileTypeCategory.Other, false)]
    [InlineData("README", ShareFileResponsePolicy.DefaultContentType, ShareFileTypeCategory.Other, false)]
    public void Resolve_ShouldReturnExpectedFileResponsePolicy(
        string fileName,
        string contentType,
        ShareFileTypeCategory category,
        bool preferInline)
    {
        var metadata = ShareFileResponsePolicy.Resolve(fileName);

        Assert.Equal(contentType, metadata.ContentType);
        Assert.Equal(category, metadata.Category);
        Assert.Equal(preferInline, metadata.PreferInline);
    }

    [Fact]
    public void Resolve_ShouldRespectConfiguredBrowserBehavior()
    {
        var settings = new AppSettings
        {
            OpenImagesInBrowser = false,
            OpenVideosInBrowser = false,
            OpenPdfInBrowser = false,
        };

        var imageMetadata = ShareFileResponsePolicy.Resolve("photo.jpg", settings);
        var videoMetadata = ShareFileResponsePolicy.Resolve("clip.mp4", settings);
        var pdfMetadata = ShareFileResponsePolicy.Resolve("report.pdf", settings);

        Assert.False(imageMetadata.PreferInline);
        Assert.False(videoMetadata.PreferInline);
        Assert.False(pdfMetadata.PreferInline);
    }

    [Theory]
    [InlineData("report.csv")]
    [InlineData("README")]
    [InlineData("export.json")]
    public void IsBrowserCompressionCandidate_ShouldAllowLikelyCompressibleAttachments(string fileName)
    {
        var metadata = ShareFileResponsePolicy.Resolve(fileName);

        Assert.True(ShareFileResponsePolicy.IsBrowserCompressionCandidate(fileName, metadata));
    }

    [Theory]
    [InlineData("photo.jpg")]
    [InlineData("clip.mp4")]
    [InlineData("report.pdf")]
    [InlineData("archive.zip")]
    [InlineData("workbook.xlsx")]
    public void IsBrowserCompressionCandidate_ShouldSkipInlineAndAlreadyCompressedFiles(string fileName)
    {
        var metadata = ShareFileResponsePolicy.Resolve(fileName);

        Assert.False(ShareFileResponsePolicy.IsBrowserCompressionCandidate(fileName, metadata));
    }
}
