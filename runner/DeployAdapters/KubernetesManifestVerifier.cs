using System.Text;
using System.Text.Json;

namespace AgentRunner.DeployAdapters;

/// <summary>Checks the parsed objects that kubectl would apply, before any mutation.</summary>
internal static class KubernetesManifestVerifier
{
    private static readonly HashSet<string> NamespacedKinds = new(StringComparer.Ordinal)
    {
        "Deployment", "Service", "ConfigMap", "Secret", "ServiceAccount", "Ingress",
        "HorizontalPodAutoscaler", "PodDisruptionBudget", "PersistentVolumeClaim",
        "Role", "RoleBinding", "NetworkPolicy", "Job", "CronJob",
    };

    public static async Task VerifyAsync(IDeployCommandRunner commands, string kubeconfig,
        string ns, string source, string workload, string image, CancellationToken ct)
    {
        var parsed = await commands.RunAsync("kubectl", ["--kubeconfig", kubeconfig, "--namespace", ns,
            "apply", "--dry-run=client", "--validate=false", "-o", "json", "--recursive", "-f", source], null, ct);
        ValidateObjects(ParseObjects(parsed.Output), ns, workload, image);
    }

    private static void ValidateObjects(JsonElement[] objects, string ns, string workload, string image)
    {
            if (objects.Length == 0) throw new InvalidDataException("Kubernetes package contains no objects.");
            var selected = 0;
            foreach (var item in objects)
            {
                var kind = RequiredString(item, "kind");
                if (!NamespacedKinds.Contains(kind))
                    throw new InvalidDataException("Kubernetes package contains an unsupported object kind.");
                if (!item.TryGetProperty("metadata", out var metadata))
                    throw new InvalidDataException("Kubernetes object has no metadata.");
                var name = RequiredString(metadata, "name");
                if (metadata.TryGetProperty("annotations", out var annotations)
                    && annotations.ValueKind == JsonValueKind.Object
                    && annotations.TryGetProperty("helm.sh/hook", out _))
                    throw new InvalidDataException("Kubernetes package contains a Helm hook that cannot be verified.");
                if (metadata.TryGetProperty("namespace", out var declaredNamespace)
                    && declaredNamespace.ValueKind != JsonValueKind.Null
                    && declaredNamespace.GetString() != ns)
                    throw new InvalidDataException("Kubernetes object targets another namespace.");
                if (kind != "Deployment" || name != workload) continue;
                selected++;
                if (!item.TryGetProperty("spec", out var spec)
                    || !spec.TryGetProperty("template", out var template)
                    || !template.TryGetProperty("spec", out var podSpec)
                    || !podSpec.TryGetProperty("containers", out var containers)
                    || containers.ValueKind != JsonValueKind.Array
                    || containers.GetArrayLength() == 0
                    || containers.EnumerateArray().Any(container =>
                        !container.TryGetProperty("image", out var value) || value.GetString() != image))
                    throw new InvalidDataException("Kubernetes workload does not use the release image.");
            }
            if (selected != 1)
                throw new InvalidDataException("Kubernetes package must contain one intended deployment.");
    }

    private static JsonElement[] ParseObjects(string output)
    {
        try
        {
            var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(output),
                new JsonReaderOptions { AllowMultipleValues = true });
            var objects = new List<JsonElement>();
            while (reader.Read())
            {
                using var document = JsonDocument.ParseValue(ref reader);
                var root = document.RootElement;
                if (root.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
                    objects.AddRange(items.EnumerateArray().Select(item => item.Clone()));
                else objects.Add(root.Clone());
            }
            return [.. objects];
        }
        catch (JsonException ex) { throw new InvalidDataException("Kubernetes objects could not be parsed.", ex); }
    }

    private static string RequiredString(JsonElement item, string key)
        => item.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()! : throw new InvalidDataException("Kubernetes object is missing " + key + ".");
}
