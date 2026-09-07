using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentStudio.ModelMigrations;

/// <summary>
/// Bounded read/write access to model-valued configuration pins. Only the
/// known model keys are exposed, and writes use compare-and-set semantics so
/// an operator edit made after a proposal was rendered is never overwritten.
/// </summary>
public sealed class ModelConfigurationPinStore
{
    private static readonly object FileLock = new();

    public static readonly IReadOnlySet<string> SupportedKeys = new HashSet<string>(
        [
            "ClaudeCli:SummaryModel",
            "CodeReviewStep:DefaultModel",
            "CodexCli:DefaultModel",
            "CodexCli:Model",
            "GlobalOrchestrator:Model",
            "PromptEnhancement:Model",
            "ProposalManagement:Model",
            "ReviewDecisionOrchestrator:AspectModel",
            "ReviewDecisionOrchestrator:Model",
            "Supervisor:SoftReasoningModel",
            "TaskSpawnerStep:DefaultModel",
            "TitleGeneration:Model",
            "WikiSearch:Model",
        ],
        StringComparer.OrdinalIgnoreCase);

    private readonly IConfiguration _configuration;
    private readonly IHostEnvironment _environment;
    private readonly ILogger<ModelConfigurationPinStore> _logger;

    public ModelConfigurationPinStore(
        IConfiguration configuration,
        IHostEnvironment environment,
        ILogger<ModelConfigurationPinStore> logger)
    {
        _configuration = configuration;
        _environment = environment;
        _logger = logger;
    }

    public IReadOnlyDictionary<string, string> GetPins()
        => SupportedKeys
            .Select(key => (Key: key, Value: _configuration[key]?.Trim()))
            .Where(item => !string.IsNullOrWhiteSpace(item.Value))
            .ToDictionary(item => item.Key, item => item.Value!, StringComparer.OrdinalIgnoreCase);

    public bool TryApply(string key, string expectedFromModel, string targetModel)
    {
        if (!SupportedKeys.Contains(key)
            || string.IsNullOrWhiteSpace(expectedFromModel)
            || string.IsNullOrWhiteSpace(targetModel))
        {
            return false;
        }

        lock (FileLock)
        {
            var current = _configuration[key]?.Trim();
            if (!SameModel(current, expectedFromModel)) return false;

            var path = Path.Combine(_environment.ContentRootPath, "appsettings.Local.json");
            var root = ReadRoot(path);
            SetNodeAtPath(root, key, JsonValue.Create(targetModel.Trim()));

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var serialized = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            var temporaryPath = path + ".model-migration.tmp";
            File.WriteAllText(temporaryPath, serialized);
            try
            {
                File.Replace(temporaryPath, path, destinationBackupFileName: null);
            }
            catch (FileNotFoundException)
            {
                File.Move(temporaryPath, path);
            }

            if (_configuration is IConfigurationRoot configurationRoot)
                configurationRoot.Reload();

            _logger.LogInformation(
                "model-configuration-pin-migrated key={ConfigKey} from={FromModel} to={ToModel}",
                key,
                expectedFromModel,
                targetModel);
            return true;
        }
    }

    private static JsonObject ReadRoot(string path)
    {
        if (!File.Exists(path)) return new JsonObject();
        var text = File.ReadAllText(path);
        if (string.IsNullOrWhiteSpace(text)) return new JsonObject();
        return JsonNode.Parse(text) as JsonObject
               ?? throw new InvalidDataException("appsettings.Local.json must contain a JSON object.");
    }

    private static void SetNodeAtPath(JsonObject root, string key, JsonNode? value)
    {
        var segments = key.Split(':');
        var cursor = root;
        for (var index = 0; index < segments.Length - 1; index++)
        {
            var segment = segments[index];
            if (cursor[segment] is JsonObject existing)
            {
                cursor = existing;
                continue;
            }

            var created = new JsonObject();
            cursor[segment] = created;
            cursor = created;
        }
        cursor[segments[^1]] = value;
    }

    private static bool SameModel(string? left, string? right)
        => string.Equals(
            ModelMetadataRegistry.NormalizeId(left),
            ModelMetadataRegistry.NormalizeId(right),
            StringComparison.OrdinalIgnoreCase);
}
