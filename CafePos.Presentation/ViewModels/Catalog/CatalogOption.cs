namespace CafePos.Presentation.ViewModels;

/// <summary>
/// Row of an optional-reference picker: either a real entity or the explicit "none" choice.
/// A MAUI <c>Picker</c> bound to <c>SelectedItem</c> has no way back to null — once a value is
/// picked, selecting nothing is impossible, so every optional link (category, modifier group)
/// needs its own empty row to be undoable.
/// </summary>
/// <typeparam name="T">Referenced entity type.</typeparam>
public sealed record CatalogOption<T>(T? Value, string Name) where T : class
{
    /// <summary>The "none" row: <see cref="Value"/> is null.</summary>
    public bool IsNone => Value is null;
}

/// <summary>Builds the row lists of the optional-reference pickers.</summary>
public static class CatalogOptions
{
    /// <summary>Builds the rows for a picker: the "none" row first, then one row per entity.</summary>
    public static List<CatalogOption<T>> WithNone<T>(this IEnumerable<T> items, string noneText, Func<T, string> nameSelector)
        where T : class
    {
        var options = new List<CatalogOption<T>>(capacity: 1) { new(null, noneText) };
        options.AddRange(items.Select(item => new CatalogOption<T>(item, nameSelector(item))));
        return options;
    }
}
