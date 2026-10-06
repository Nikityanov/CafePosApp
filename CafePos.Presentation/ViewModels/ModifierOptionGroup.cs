using CafePos.Core.Models;

namespace CafePos.Presentation.ViewModels;

/// <summary>
/// Group class for MAUI CollectionView grouped display.
/// Inherits List&lt;ModifierOption&gt; so CollectionView treats it as a group of items.
/// Name property is used in GroupHeaderTemplate.
/// </summary>
public sealed class ModifierOptionGroup : List<ModifierOption>
{
    /// <summary>Id of the underlying modifier group (needed to edit/delete the whole group).</summary>
    public Guid GroupId { get; }

    public string Name { get; }

    public ModifierOptionGroup(Guid groupId, string name, IEnumerable<ModifierOption> options) : base(options)
    {
        GroupId = groupId;
        Name = name;
    }
}
