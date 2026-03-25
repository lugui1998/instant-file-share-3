using InstantFileShare.Core;

namespace InstantFileShare.Core.Tests;

public sealed class ShareUtilitiesTests
{
    [Fact]
    public void Generate_ShouldProduceBase62TokenWithMinimumLength()
    {
        var token = ShareTokenGenerator.Generate();

        Assert.True(token.Length >= 22);
        Assert.Matches("^[0-9A-Za-z]+$", token);
    }

    [Fact]
    public void CreateSlug_ShouldNormalizeReadableSlug()
    {
        var slug = FileNameSlug.Create("Quarterly Report (Final) 2026.pdf");

        Assert.Equal("quarterly-report-final-2026", slug);
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
}
