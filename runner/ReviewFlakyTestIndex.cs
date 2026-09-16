using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using AgentStudio.TaskServer.Contracts;

namespace AgentRunner;

/// <summary>
/// Reads the xUnit <c>Category=ReviewFlaky</c> contract from the test assemblies
/// produced by an exact-subject review command. The index is evidence only: an
/// unreadable assembly or an unknown test stays outside quarantine and therefore
/// keeps the normal baseline-comparison and retry path.
/// </summary>
internal sealed class ReviewFlakyTestIndex
{
    internal const string TraitName = "Category";
    internal const string TraitValue = "ReviewFlaky";
    /// <summary>
    /// The shared classification both flaky surfaces write (AGT-2853). Kept as
    /// an alias so existing call sites read the executor's own vocabulary.
    /// </summary>
    internal const string VerdictClassification = ReviewFlakyQuarantine.Classification;

    private readonly HashSet<string> _types;
    private readonly HashSet<string> _methods;

    internal ReviewFlakyTestIndex(
        IEnumerable<string>? types = null,
        IEnumerable<string>? methods = null)
    {
        _types = new HashSet<string>(types ?? [], StringComparer.Ordinal);
        _methods = new HashSet<string>(methods ?? [], StringComparer.Ordinal);
    }

    internal static ReviewFlakyTestIndex Discover(string repositoryPath, Action<string>? log = null)
    {
        var types = new HashSet<string>(StringComparer.Ordinal);
        var methods = new HashSet<string>(StringComparer.Ordinal);
        if (!Directory.Exists(repositoryPath)) return new ReviewFlakyTestIndex(types, methods);

        foreach (var assemblyPath in Directory.EnumerateFiles(
                     repositoryPath, "*.dll", SearchOption.AllDirectories))
        {
            if (!IsCandidateAssembly(repositoryPath, assemblyPath)) continue;
            try
            {
                ReadAssembly(assemblyPath, types, methods);
            }
            catch (Exception exception) when (exception is IOException
                                              or UnauthorizedAccessException
                                              or BadImageFormatException
                                              or InvalidOperationException)
            {
                log?.Invoke(
                    $"review flaky trait index skipped assembly={assemblyPath}: {exception.Message}");
            }
        }

        log?.Invoke($"review flaky trait index types={types.Count} methods={methods.Count}");
        return new ReviewFlakyTestIndex(types, methods);
    }

    internal bool Contains(string failureName)
    {
        var normalized = failureName.Trim();
        if (_methods.Contains(normalized)) return true;
        if (_methods.Any(method => normalized.StartsWith(method + "(", StringComparison.Ordinal)))
            return true;
        return _types.Any(type => normalized.StartsWith(type + ".", StringComparison.Ordinal));
    }

    private static bool IsCandidateAssembly(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        var segments = relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        if (!segments.Contains("bin", StringComparer.OrdinalIgnoreCase)) return false;
        if (segments.Contains("ref", StringComparer.OrdinalIgnoreCase)
            || segments.Contains("refint", StringComparer.OrdinalIgnoreCase)
            || segments.Contains("runtimes", StringComparer.OrdinalIgnoreCase))
            return false;
        return true;
    }

    private static void ReadAssembly(
        string assemblyPath,
        ISet<string> types,
        ISet<string> methods)
    {
        using var stream = File.OpenRead(assemblyPath);
        using var peReader = new PEReader(stream, PEStreamOptions.LeaveOpen);
        if (!peReader.HasMetadata) return;
        var reader = peReader.GetMetadataReader();
        var methodOwners = MethodOwners(reader);

        foreach (var attributeHandle in reader.CustomAttributes)
        {
            var attribute = reader.GetCustomAttribute(attributeHandle);
            if (!IsXunitTrait(reader, attribute.Constructor, methodOwners)
                || !TryReadTrait(reader, attribute, out var name, out var value)
                || !string.Equals(name, TraitName, StringComparison.Ordinal)
                || !string.Equals(value, TraitValue, StringComparison.Ordinal))
                continue;

            switch (attribute.Parent.Kind)
            {
                case HandleKind.TypeDefinition:
                    types.Add(TypeName(reader, (TypeDefinitionHandle)attribute.Parent));
                    break;
                case HandleKind.MethodDefinition:
                {
                    var methodHandle = (MethodDefinitionHandle)attribute.Parent;
                    if (!methodOwners.TryGetValue(methodHandle, out var owner)) break;
                    var method = reader.GetMethodDefinition(methodHandle);
                    methods.Add($"{TypeName(reader, owner)}.{reader.GetString(method.Name)}");
                    break;
                }
            }
        }
    }

    private static Dictionary<MethodDefinitionHandle, TypeDefinitionHandle> MethodOwners(
        MetadataReader reader)
    {
        var owners = new Dictionary<MethodDefinitionHandle, TypeDefinitionHandle>();
        foreach (var typeHandle in reader.TypeDefinitions)
        {
            foreach (var methodHandle in reader.GetTypeDefinition(typeHandle).GetMethods())
                owners[methodHandle] = typeHandle;
        }
        return owners;
    }

    private static bool IsXunitTrait(
        MetadataReader reader,
        EntityHandle constructor,
        IReadOnlyDictionary<MethodDefinitionHandle, TypeDefinitionHandle> methodOwners)
    {
        EntityHandle attributeType;
        switch (constructor.Kind)
        {
            case HandleKind.MemberReference:
                attributeType = reader.GetMemberReference((MemberReferenceHandle)constructor).Parent;
                break;
            case HandleKind.MethodDefinition:
                if (!methodOwners.TryGetValue((MethodDefinitionHandle)constructor, out var owner))
                    return false;
                attributeType = owner;
                break;
            default:
                return false;
        }

        return attributeType.Kind switch
        {
            HandleKind.TypeReference => IsXunitTrait(reader, reader.GetTypeReference(
                (TypeReferenceHandle)attributeType)),
            HandleKind.TypeDefinition => IsXunitTrait(reader, reader.GetTypeDefinition(
                (TypeDefinitionHandle)attributeType)),
            _ => false,
        };
    }

    private static bool IsXunitTrait(MetadataReader reader, TypeReference type)
        => string.Equals(reader.GetString(type.Namespace), "Xunit", StringComparison.Ordinal)
           && string.Equals(reader.GetString(type.Name), "TraitAttribute", StringComparison.Ordinal);

    private static bool IsXunitTrait(MetadataReader reader, TypeDefinition type)
        => string.Equals(reader.GetString(type.Namespace), "Xunit", StringComparison.Ordinal)
           && string.Equals(reader.GetString(type.Name), "TraitAttribute", StringComparison.Ordinal);

    private static bool TryReadTrait(
        MetadataReader reader,
        CustomAttribute attribute,
        out string? name,
        out string? value)
    {
        name = null;
        value = null;
        try
        {
            var blob = reader.GetBlobReader(attribute.Value);
            if (blob.ReadUInt16() != 1) return false;
            name = blob.ReadSerializedString();
            value = blob.ReadSerializedString();
            return name is not null && value is not null;
        }
        catch (BadImageFormatException)
        {
            return false;
        }
    }

    private static string TypeName(MetadataReader reader, TypeDefinitionHandle handle)
    {
        var type = reader.GetTypeDefinition(handle);
        var name = reader.GetString(type.Name);
        var declaringType = type.GetDeclaringType();
        if (!declaringType.IsNil)
            return $"{TypeName(reader, declaringType)}.{name}";
        var typeNamespace = reader.GetString(type.Namespace);
        return typeNamespace.Length == 0 ? name : $"{typeNamespace}.{name}";
    }
}
