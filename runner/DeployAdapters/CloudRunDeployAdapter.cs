using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentRunner.DeployAdapters;

/// <summary>Deploys the immutable image to Cloud Run using keyless workload federation.</summary>
public sealed partial class CloudRunDeployAdapter(IDeploySecretResolver secrets, IDeployCommandRunner commands)
    : IDeployTargetAdapter
{
    public string Kind => "cloud-run";

    public async Task DeployAsync(DeployTargetRequest request, CancellationToken ct)
    {
        DeployArtifactVerifier.Verify(request);
        if (request.Step.Step != "start-container")
            throw new InvalidDataException("Cloud Run target needs a start-container step.");
        var image = DeployTargetValidation.Image(request.Artifact);
        var project = DeployTargetValidation.Identifier(request.Target.Project);
        var region = DeployTargetValidation.Identifier(request.Target.Region);
        var service = DeployTargetValidation.Identifier(request.Target.CloudService);
        var poolProject = request.Target.IdentityPoolProjectNumber;
        if (poolProject is null || !PoolProjectNumber().IsMatch(poolProject))
            throw new InvalidDataException("Cloud Run needs a workload pool project number.");
        var pool = DeployTargetValidation.Identifier(request.Target.IdentityPool);
        var provider = DeployTargetValidation.Identifier(request.Target.IdentityProvider);
        var account = request.Target.CloudServiceAccount;
        if (account is null || !ServiceAccount().IsMatch(account))
            throw new InvalidDataException("Cloud Run needs a valid impersonated service account.");
        using var temp = new DeployTempDirectory();
        var subjectToken = await temp.SecretFileAsync("subject.jwt",
            await secrets.ResolveAsync(request.Target.CredentialSecret, ct), ct);
        var audience = "//iam.googleapis.com/projects/" + poolProject
            + "/locations/global/workloadIdentityPools/" + pool + "/providers/" + provider;
        var credential = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["type"] = "external_account",
            ["audience"] = audience,
            ["subject_token_type"] = "urn:ietf:params:oauth:token-type:jwt",
            ["token_url"] = "https://sts.googleapis.com/v1/token",
            ["credential_source"] = new { file = subjectToken },
            ["service_account_impersonation_url"] =
                "https://iamcredentials.googleapis.com/v1/projects/-/serviceAccounts/"
                + account + ":generateAccessToken",
        });
        var credentialFile = await temp.SecretFileAsync("cloud-federation.json", credential, ct);
        var environment = new Dictionary<string, string> { ["CLOUDSDK_CONFIG"] = temp.Path };
        await commands.RunAsync("gcloud", ["--quiet", "auth", "login", "--cred-file", credentialFile],
            environment, ct);
        await commands.RunAsync("gcloud", ["--quiet", "run", "deploy", service, "--image", image,
            "--project", project, "--region", region, "--platform", "managed",
            "--no-allow-unauthenticated"], environment, ct);
    }

    [GeneratedRegex(@"\A[1-9][0-9]{5,19}\z", RegexOptions.CultureInvariant)]
    private static partial Regex PoolProjectNumber();

    [GeneratedRegex(@"\A[a-z][a-z0-9-]{2,62}@[a-z][a-z0-9-]{2,62}\.iam\.gserviceaccount\.com\z",
        RegexOptions.CultureInvariant)]
    private static partial Regex ServiceAccount();
}
