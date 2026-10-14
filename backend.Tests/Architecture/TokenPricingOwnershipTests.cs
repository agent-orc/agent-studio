using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Xunit;

namespace AgentStudio.Tests.Architecture;

[Trait("Category", "Guard")]
public sealed class TokenPricingOwnershipTests
{
    [Fact]
    public void HistoricalPricing_HasOneProviderAdapterAndNoStaticFacade()
    {
        var root = BackendRoot();
        var files = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains("/obj/") && !path.Contains("/bin/"))
            .Select(path => (Path: path, Source: File.ReadAllText(path)))
            .ToList();
        var owners = files.Where(file => file.Source.Contains(".ComputeCost("))
            .Select(file => Path.GetFileName(file.Path)).ToArray();
        Assert.Equal(["TokenEconomyPriceProvider.cs"], owners);
        var catalogs = files.Where(file => file.Source.Contains("ModelPriceCatalog.Default"))
            .Select(file => Path.GetFileName(file.Path)).ToArray();
        Assert.Equal(["TokenEconomyPriceProvider.cs"], catalogs);
        Assert.DoesNotContain(files, file => Regex.IsMatch(file.Source, @"\bTokenPricing\.(Estimate|Price)\("));
        Assert.DoesNotContain(files, file => Regex.IsMatch(file.Source, @"new\s+(TokenEconomyPriceProvider|TokenPricingService)\("));
        var folds = files.Where(file => file.Path.Replace('\\', '/').Contains("/Tokens/Aggregation/"));
        Assert.DoesNotContain(folds, file => Regex.IsMatch(file.Source, @"\b(?:ITokenPricing|ITokenPriceProvider)\s+\w+|\.Estimate\("));
    }

    private static string BackendRoot([CallerFilePath] string source = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "..", "..", "backend"));
}
