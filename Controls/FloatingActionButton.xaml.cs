using System.Windows.Input;

namespace CafePosApp.Controls;

/// <summary>
/// The catalogue page's add action: an extended floating action button — a Material "add"
/// mark plus a short label. See the markup comment for why the root is a ContentView with a
/// transparent Button over the pill rather than a Button carrying its own content.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Command"/>, <see cref="CommandParameter"/> and <see cref="Label"/> are declared
/// here and pushed into the inner button and label, so the page's markup reads the same as it
/// did when the control was a Button. <see cref="Description"/> is a second, separate member
/// because the label is already visible text: it is what a screen reader announces.
/// </para>
/// <para>
/// <see cref="VisualElement.IsEnabled"/> is deliberately *not* forwarded. The button already disables itself
/// from <c>Command.CanExecute</c>, which is the only thing in this app that should turn the add
/// action off; forwarding the container's own flag would fight the disabled state MAUI derives
/// from the command.
/// </para>
/// </remarks>
public partial class FloatingActionButton : ContentView
{
    public static readonly BindableProperty CommandProperty =
        BindableProperty.Create(nameof(Command), typeof(ICommand), typeof(FloatingActionButton));

    public static readonly BindableProperty CommandParameterProperty =
        BindableProperty.Create(nameof(CommandParameter), typeof(object), typeof(FloatingActionButton));

    public static readonly BindableProperty LabelProperty =
        BindableProperty.Create(nameof(Label), typeof(string), typeof(FloatingActionButton));

    public static readonly BindableProperty DescriptionProperty =
        BindableProperty.Create(nameof(Description), typeof(string), typeof(FloatingActionButton));

    public ICommand? Command
    {
        get => (ICommand?)GetValue(CommandProperty);
        set => SetValue(CommandProperty, value);
    }

    public object? CommandParameter
    {
        get => GetValue(CommandParameterProperty);
        set => SetValue(CommandParameterProperty, value);
    }

    /// <summary>
    /// Visible label beside the mark. The control's callers own its wording; the switch between
    /// what the button adds lives in the ViewModel, not here.
    /// </summary>
    public string? Label
    {
        get => (string?)GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    /// <summary>
    /// Screen-reader name. The mark plus label read as the action to a sighted user, so a
    /// screen reader must get words too — and it must name the action, not the shape. Set on
    /// the inner button, because that is the view TalkBack and VoiceOver actually focus:
    /// leaving it on the container would announce nothing, since a container with children is
    /// not itself an accessibility element.
    /// </summary>
    public string? Description
    {
        get => (string?)GetValue(DescriptionProperty);
        set
        {
            SetValue(DescriptionProperty, value);
            SemanticProperties.SetDescription(TouchTarget, value);
        }
    }

    public FloatingActionButton()
    {
        InitializeComponent();

        // One-way bindings out to the children, rather than property-changed handlers in both
        // directions: the children are private to this control, so nothing can write them
        // behind the control's back and there is no loop to guard against.
        TouchTarget.SetBinding(Button.CommandProperty, new Binding(nameof(Command), source: this));
        TouchTarget.SetBinding(Button.CommandParameterProperty, new Binding(nameof(CommandParameter), source: this));

        // MAUI's SemanticProperties exposes only Description and Hint — there is no role API.
        // A Button already announces itself as activatable, so only the hint needed adding.
        SemanticProperties.SetHint(TouchTarget, "Двойное нажатие — открыть форму");
    }
}