using System.Xml;
using System.Xml.Linq;

namespace AgentStudio.Runner;

/// <summary>Conservative classification of test project inventory changes.</summary>
internal static class ReviewInventoryChangePolicy
{
    internal static bool IsContentOnlyTestProjectChange(string? before, string? after)
    {
        if (before is null || after is null) return false;
        try
        {
            var oldProject = XDocument.Parse(before);
            var newProject = XDocument.Parse(after);
            if (XNode.DeepEquals(oldProject, newProject)) return false;
            var oldItems = ContentItems(oldProject);
            var newItems = ContentItems(newProject);
            if (oldItems.Select(ItemKey).Order(StringComparer.Ordinal).SequenceEqual(
                    newItems.Select(ItemKey).Order(StringComparer.Ordinal), StringComparer.Ordinal))
                return false;
            // Copy metadata on an existing identity is an edit, not an item
            // addition or removal. A shared identity must remain unchanged.
            foreach (var item in oldItems.Concat(newItems))
            {
                var other = oldItems.Contains(item) ? newItems : oldItems;
                if (other.Any(candidate => ItemKey(candidate) == ItemKey(item))
                    && !other.Any(candidate => XNode.DeepEquals(candidate, item)))
                    return false;
            }
            StripContentItems(oldProject);
            StripContentItems(newProject);
            return XNode.DeepEquals(oldProject, newProject);
        }
        catch (XmlException)
        {
            return false;
        }
    }

    private static XElement[] ContentItems(XDocument project)
        => (project.Root?.Elements("ItemGroup") ?? [])
            .SelectMany(group => group.Elements().Where(IsCopyContentItem))
            .ToArray();

    private static string ItemKey(XElement item)
        => item.Name.LocalName + ":" + item.Attributes()
            .Single(attribute => attribute.Name.LocalName is "Include" or "Remove").Value;

    private static void StripContentItems(XDocument project)
    {
        foreach (var group in project.Root?.Elements("ItemGroup") ?? [])
        {
            foreach (var item in group.Elements().Where(IsCopyContentItem).ToArray())
                item.Remove();
            // A newly added group containing only content items is harmless.
            if (!group.HasElements && !group.HasAttributes)
                group.Remove();
        }
    }

    private static bool IsCopyContentItem(XElement item)
        => item.Name.LocalName is "None" or "Content" or "EmbeddedResource"
           && item.Name.NamespaceName.Length == 0
           && item.Attributes().Count(attribute => attribute.Name.LocalName is "Include" or "Remove") == 1
           && item.Attributes().All(attribute => attribute.Name.NamespaceName.Length == 0
               && (attribute.Name.LocalName is "Include" or "Remove"
                   or "CopyToOutputDirectory" or "CopyToPublishDirectory"))
           && item.Elements().All(child => (child.Name.LocalName is
               "CopyToOutputDirectory" or "CopyToPublishDirectory")
               && child.Name.NamespaceName.Length == 0
               && !child.HasAttributes && !child.HasElements);
}
