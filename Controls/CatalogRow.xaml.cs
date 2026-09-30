using System.Windows.Input;

namespace CafePosApp.Controls;

/// <summary>
/// The shared skeleton of a catalogue list row: a card, a long-pressable content column and an
/// overflow button whose mark comes from the <c>RowOverflowButton</c> control template.
/// </summary>
/// <remarks>
/// Mirrors <see cref="FloatingActionButton"/>: because the markup lives inside a reusable control
/// rather than in a CollectionView DataTemplate, the commands are handed in as bindable
/// properties instead of being reached through a long <c>{x:Reference Page}.BindingContext.X</c>
/// path (which is what the row templates used to repeat five times).
/// <para>
/// A primary tap is deliberately <b>not</b> handled here. The page binds the enclosing
/// CollectionView's SelectionMode to the "select" mode and reads the selection there, so a tap
/// is a single, arbitrated gesture shared with the long press; a TapGestureRecognizer inside the
/// row would compete with the list's own selection handling.
/// </para>
/// </remarks>
public partial class CatalogRow : ContentView
{
    public CatalogRow() => InitializeComponent();

    /// <summary>Long press → the row's overflow sheet (edit / copy / delete / toggle).</summary>
    public static readonly BindableProperty LongPressCommandProperty =
        BindableProperty.Create(nameof(LongPressCommand), typeof(ICommand), typeof(CatalogRow));

    public static readonly BindableProperty OverflowCommandProperty =
        BindableProperty.Create(nameof(OverflowCommand), typeof(ICommand), typeof(CatalogRow));

    public static readonly BindableProperty CommandParameterProperty =
        BindableProperty.Create(nameof(CommandParameter), typeof(object), typeof(CatalogRow));

    public static readonly BindableProperty RowContentProperty =
        BindableProperty.Create(nameof(RowContent), typeof(View), typeof(CatalogRow));

    public static readonly BindableProperty OverflowDescriptionProperty =
        BindableProperty.Create(nameof(OverflowDescription), typeof(string), typeof(CatalogRow));

    public static readonly BindableProperty IsHeaderProperty =
        BindableProperty.Create(nameof(IsHeader), typeof(bool), typeof(CatalogRow));

    public ICommand? LongPressCommand
    {
        get => (ICommand?)GetValue(LongPressCommandProperty);
        set => SetValue(LongPressCommandProperty, value);
    }

    public ICommand? OverflowCommand
    {
        get => (ICommand?)GetValue(OverflowCommandProperty);
        set => SetValue(OverflowCommandProperty, value);
    }

    public object? CommandParameter
    {
        get => GetValue(CommandParameterProperty);
        set => SetValue(CommandParameterProperty, value);
    }

    /// <summary>The row-specific part (name, price, pills) declared by the caller's DataTemplate.</summary>
    public View? RowContent
    {
        get => (View?)GetValue(RowContentProperty);
        set => SetValue(RowContentProperty, value);
    }

    public string? OverflowDescription
    {
        get => (string?)GetValue(OverflowDescriptionProperty);
        set => SetValue(OverflowDescriptionProperty, value);
    }

    /// <summary>Renders the filled Primary group header used by the modifier-group list.</summary>
    public bool IsHeader
    {
        get => (bool)GetValue(IsHeaderProperty);
        set => SetValue(IsHeaderProperty, value);
    }
}
