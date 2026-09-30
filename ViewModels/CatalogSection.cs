using CommunityToolkit.Mvvm.ComponentModel;

namespace CafePosApp.ViewModels;

/// <summary>One entity switcher entry in the catalogue header (Товары / Разделы / …).</summary>
/// <remarks>
/// The switcher used to be four hand-placed Buttons, each with its own keyed style whose only
/// difference was a DataTrigger on that section's IsVisible flag — four styles that had to be
/// kept paired 1:1 with four buttons. Driving the strip from this list lets one chip template
/// (and one style) cover all of them, and makes the strip wrap instead of clipping.
/// </remarks>
public sealed partial class CatalogSection : ObservableObject
{
    public CatalogSection(string key, string label)
    {
        Key = key;
        Label = label;
    }

    /// <summary>Value passed to SetSectionCommand.</summary>
    public string Key { get; }

    public string Label { get; }

    private bool isSelected;
    public bool IsSelected
    {
        get => isSelected;
        set => SetProperty(ref isSelected, value);
    }
}
