using AgentStudio.Shared;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// The checkout boundary every file-reading route shares: a regular file
/// inside the checkout is readable; a link, Git metadata, a directory and an
/// oversize file are not.
/// </summary>
public sealed class FilesystemPathBoundaryCheckoutTests : IDisposable
{
    private readonly CheckoutFileFixture _checkout = new("checkout-boundary");

    public void Dispose() => _checkout.Dispose();

    [Fact]
    public void RegularFileInsideCheckout_IsReadable()
    {
        var full = _checkout.Write("docs/readme.md", "inside");

        Assert.Equal(CheckoutFileStatus.Readable, FilesystemPathBoundary.CheckCheckoutFile(_checkout.Root, full, 1024));
    }

    [Fact]
    public void MissingFileInsideCheckout_IsMissing()
    {
        Assert.Equal(CheckoutFileStatus.Missing,
            FilesystemPathBoundary.CheckCheckoutFile(_checkout.Root, _checkout.Full("docs/absent.md"), 1024));
    }

    [Fact]
    public void FileAboveLimit_IsTooLarge()
    {
        var full = _checkout.WriteOversize("docs/big.bin", 1024);

        Assert.Equal(CheckoutFileStatus.TooLarge, FilesystemPathBoundary.CheckCheckoutFile(_checkout.Root, full, 1024));
    }

    [Fact]
    public void DirectoryAndOutsidePath_AreRefused()
    {
        Directory.CreateDirectory(_checkout.Full("docs"));
        var outside = _checkout.OutsideFile("secret.md", "outside");

        Assert.Equal(CheckoutFileStatus.Refused,
            FilesystemPathBoundary.CheckCheckoutFile(_checkout.Root, _checkout.Full("docs"), 1024));
        Assert.Equal(CheckoutFileStatus.Refused,
            FilesystemPathBoundary.CheckCheckoutFile(_checkout.Root, outside, 1024));
    }

    [Theory]
    [InlineData(".git/config")]
    [InlineData(".GIT/config")]
    [InlineData("vendor/lib/.git/config")]
    [InlineData(".git")]
    public void GitMetadata_IsRefused(string relativePath)
    {
        var full = _checkout.Full(relativePath);
        if (!File.Exists(full) && !Directory.Exists(full)) _checkout.Write(relativePath, "[core]\n");

        Assert.Equal(CheckoutFileStatus.Refused, FilesystemPathBoundary.CheckCheckoutFile(_checkout.Root, full, 1024));
    }

    [Theory]
    [InlineData(".git/config", true)]
    [InlineData(".git./config", true)]
    [InlineData(".git /config", true)]
    [InlineData("GIT~1/config", true)]
    [InlineData("git~12/config", true)]
    [InlineData("docs/.github/workflow.yml", false)]
    [InlineData("docs/.gitignore", false)]
    [InlineData("docs/git~/notes.md", false)]
    public void GitMetadataSegments_AreRecognised(string relativePath, bool expected)
    {
        Assert.Equal(expected, FilesystemPathBoundary.IsUnderGitMetadata(_checkout.Root, _checkout.Full(relativePath)));
    }

    [Fact]
    public void CommittedLinks_AreRefused()
    {
        var outside = _checkout.OutsideFile("secret.md", "outside");
        if (!_checkout.TryCommitLink("docs/outside.md", outside)) return;
        _checkout.TryCommitLink("docs/config.md", _checkout.Full(".git/config"));

        Assert.Equal(CheckoutFileStatus.Refused,
            FilesystemPathBoundary.CheckCheckoutFile(_checkout.Root, _checkout.Full("docs/outside.md"), 1024));
        Assert.Equal(CheckoutFileStatus.Refused,
            FilesystemPathBoundary.CheckCheckoutFile(_checkout.Root, _checkout.Full("docs/config.md"), 1024));
    }
}
