using System.Diagnostics;
using System.Text;
using AgentStudio.Security;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Every credential below is assembled at runtime from fragments, so this
/// source file carries no value a push scanner would recognise.
/// </summary>
public sealed class CredentialRedactorTests
{
    private const string Masked = "[REDACTED_CREDENTIAL]";

    [Fact]
    public void Masks_github_installation_and_personal_tokens()
    {
        foreach (var prefix in new[] { "gh" + "s_", "gh" + "p_", "gh" + "o_", "gh" + "u_", "gh" + "r_", "github" + "_pat_" })
        {
            var token = prefix + Body(36);

            Assert.Equal($"push used {Masked} today", CredentialRedactor.Redact($"push used {token} today"));
        }
    }

    [Fact]
    public void Masks_access_token_user_info_in_remote_urls()
    {
        var token = Body(40);
        var user = "x-access" + "-token";

        Assert.Equal(
            "fatal: https://[REDACTED]@github.com/org/repo.git not found",
            CredentialRedactor.Redact($"fatal: https://{user}:{token}@github.com/org/repo.git not found"));
        Assert.Equal(
            $"remote {user}:[REDACTED]@github.com/org/repo",
            CredentialRedactor.Redact($"remote {user}:{token}@github.com/org/repo"));
    }

    [Fact]
    public void Masks_anthropic_keys_and_oauth_tokens()
    {
        var key = "sk-" + "ant-" + "api03-" + Body(80);
        var oauth = "sk-" + "ant-" + "oat01-" + Body(80);

        Assert.Equal($"key {Masked} and {Masked}", CredentialRedactor.Redact($"key {key} and {oauth}"));
    }

    [Fact]
    public void Masks_openai_keys()
    {
        var project = "sk-" + "proj-" + Body(48);
        var legacy = "sk-" + Body(48);

        Assert.Equal($"{Masked} {Masked}", CredentialRedactor.Redact($"{project} {legacy}"));
    }

    [Fact]
    public void Masks_claude_code_oauth_token_assignments_of_any_shape()
    {
        var name = "CLAUDE_CODE" + "_OAUTH_TOKEN";
        var value = Body(30);

        Assert.Equal($"export {name}=[REDACTED] && run", CredentialRedactor.Redact($"export {name}={value} && run"));
    }

    [Fact]
    public void Masks_refresh_tokens_in_json_and_form_bodies()
    {
        var field = "refresh" + "_token";
        var value = Body(30);

        var json = CredentialRedactor.Redact($$"""{"{{field}}": "{{value}}", "scope": "repo"}""");
        var form = CredentialRedactor.Redact($"grant_type=refresh&{field}={value}&client=x");

        Assert.DoesNotContain(value, json, StringComparison.Ordinal);
        Assert.Contains("\"scope\": \"repo\"", json, StringComparison.Ordinal);
        Assert.Equal($"grant_type=refresh&{field}=[REDACTED]&client=x", form);
    }

    [Fact]
    public void Masks_whole_private_key_blocks()
    {
        var label = "RSA " + "PRIVATE" + " KEY";
        var block = $"-----BEGIN {label}-----\n{Body(64)}\n{Body(64)}\n-----END {label}-----";

        Assert.Equal($"key:\n{Masked}\ndone", CredentialRedactor.Redact($"key:\n{block}\ndone"));
    }

    [Fact]
    public void Masks_a_truncated_private_key_block_to_the_end_of_the_text()
    {
        var label = "OPENSSH " + "PRIVATE" + " KEY";
        var body = Body(64);

        Assert.Equal($"tail {Masked}", CredentialRedactor.Redact($"tail -----BEGIN {label}-----\n{body}"));
    }

    [Fact]
    public void Masks_studio_issued_tokens_with_long_prefixes()
    {
        foreach (var prefix in new[] { "inv", "sat", "api", "rnr" })
        {
            var token = $"{prefix}.{Guid.NewGuid():N}.{PasswordSecretHasher.RandomToken()}";

            Assert.Equal($"used {Masked}.", CredentialRedactor.Redact($"used {token}."));
        }
    }

    [Fact]
    public void Leaves_ordinary_hosts_urls_and_words_alone()
    {
        const string text = "GET https://api.github.com/repos via ssh://git@github.com/org/repo, sk-learn and gh_cli";

        Assert.Equal(text, CredentialRedactor.Redact(text));
    }

    [Fact]
    public void A_long_input_without_tokens_finishes_in_bounded_time()
    {
        var user = "x-access" + "-token:";
        var text = new StringBuilder()
            .Append(string.Concat(Enumerable.Repeat(user, 40_000)))
            .Append(' ')
            .Append(string.Concat(Enumerable.Repeat("https://user:", 40_000)))
            .Append(' ')
            .Append(string.Concat(Enumerable.Repeat("-----BEGIN RSA PUBLIC KEY-----\n", 20_000)))
            .Append(string.Concat(Enumerable.Repeat("sk-short gh" + "s_short ", 40_000)))
            .ToString();

        var watch = Stopwatch.StartNew();
        var redacted = CredentialRedactor.Redact(text);
        watch.Stop();

        Assert.Equal(text, redacted);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10),
            $"Redacting {text.Length} characters took {watch.Elapsed}.");
    }

    private static string Body(int length)
    {
        const string alphabet = "aB3dE5fG7hJ9kLmN2pQ4rS6tU8vWxYz";
        var random = new Random(length);
        return string.Concat(Enumerable.Range(0, length).Select(_ => alphabet[random.Next(alphabet.Length)]));
    }
}
