using AgentStudio.Secrets;
using Xunit;

namespace AgentStudio.Tests;

public sealed class SecretReferenceTests
{
    [Theory]
    [InlineData("secret://workspace/MAIL_KEY", "workspace", null, null, "MAIL_KEY", null)]
    [InlineData("secret://memowikis/DATABASE_URL", "project", "memowikis", null, "DATABASE_URL", null)]
    [InlineData("secret://memowikis/stage/DATABASE_URL", "environment", "memowikis", "stage", "DATABASE_URL", null)]
    [InlineData("secret://memowikis/prod/DATABASE_URL@3", "environment", "memowikis", "prod", "DATABASE_URL", 3)]
    [InlineData("secret://workspace/smtp.password@12", "workspace", null, null, "smtp.password", 12)]
    public void Parses_every_scope_shape_and_round_trips(string text, string kind, string? project, string? environment, string name, int? version)
    {
        var reference = SecretReference.Parse(text);

        Assert.Equal(kind, reference.Scope.Kind.ToString().ToLowerInvariant());
        Assert.Equal(project, reference.Scope.Project);
        Assert.Equal(environment, reference.Scope.Environment);
        Assert.Equal(name, reference.Name);
        Assert.Equal(version, reference.Version);
        Assert.Equal(text, reference.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("DATABASE_URL")]
    [InlineData("vault://memowikis/DATABASE_URL")]
    [InlineData("secret://DATABASE_URL")]
    [InlineData("secret://memowikis/")]
    [InlineData("secret://memowikis/stage/extra/DATABASE_URL")]
    [InlineData("secret://memowikis/Stage/DATABASE_URL")]
    [InlineData("secret://memowikis/1_STARTS_WITH_DIGIT")]
    [InlineData("secret://memowikis/DATABASE_URL@0")]
    [InlineData("secret://memowikis/DATABASE_URL@-1")]
    [InlineData("secret://memowikis/DATABASE_URL@latest")]
    [InlineData("secret://../DATABASE_URL")]
    [InlineData("secret://workspace/stage/DATABASE_URL")]
    public void Rejects_malformed_references(string? text)
    {
        Assert.False(SecretReference.TryParse(text, out _));
        Assert.Equal("invalid-reference", Assert.Throws<SecretValidationException>(() => SecretReference.Parse(text)).Code);
    }

    [Fact]
    public void Project_named_workspace_is_reserved()
    {
        Assert.Equal("invalid-scope", Assert.Throws<SecretValidationException>(() => SecretScope.ForProject("Workspace")).Code);
    }

    [Fact]
    public void Project_scope_compares_case_insensitively_environment_and_name_do_not()
    {
        Assert.Equal(SecretScope.ForProject("MemoWikis"), SecretScope.ForProject("memowikis"));
        Assert.Equal(SecretScope.ForEnvironment("MemoWikis", "stage").GetHashCode(), SecretScope.ForEnvironment("memowikis", "stage").GetHashCode());
        Assert.NotEqual(SecretScope.ForEnvironment("memowikis", "stage"), SecretScope.ForEnvironment("memowikis", "prod"));
        Assert.NotEqual(BuiltInSecretStore.SecretId(SecretScope.Workspace, "Key"), BuiltInSecretStore.SecretId(SecretScope.Workspace, "KEY"));
        Assert.Equal(BuiltInSecretStore.SecretId(SecretScope.ForProject("MemoWikis"), "KEY"),
            BuiltInSecretStore.SecretId(SecretScope.ForProject("memowikis"), "KEY"));
    }

    [Fact]
    public void Inheritance_runs_from_the_environment_through_the_project_to_the_workspace()
    {
        Assert.Equal(["memowikis/stage", "memowikis", "workspace"],
            SecretScope.ForEnvironment("memowikis", "stage").InheritanceChain().Select(scope => scope.Key));
        Assert.Equal(["memowikis", "workspace"], SecretScope.ForProject("memowikis").InheritanceChain().Select(scope => scope.Key));
        Assert.Equal(["workspace"], SecretScope.Workspace.InheritanceChain().Select(scope => scope.Key));
    }
}
