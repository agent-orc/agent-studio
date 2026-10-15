using Xunit;

namespace AgentStudio.Tests;

public sealed class ProjectRepositoryUrlTests
{
    [Theory]
    [InlineData("https://github.com/example/demo.git")]
    [InlineData("http://git.example.test/team/demo.git")]
    public void CredentialFreeOriginIsAccepted(string url)
        => Assert.Equal(url, ProjectRegistry.ValidateRepositoryUrl(url));

    [Theory]
    [InlineData("https://user:secret@github.com/example/demo.git")]
    [InlineData("https://github.com/example/demo.git?token=secret")]
    [InlineData("https://github.com/example/demo.git#secret")]
    public void UrlCarryingASecretIsRejectedBeforePersistence(string url)
        => Assert.Throws<ArgumentException>(() => ProjectRegistry.ValidateRepositoryUrl(url));
}
