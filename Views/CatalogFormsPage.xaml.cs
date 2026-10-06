using CafePosApp.Controls;
using CafePosApp.Converters;
using CafePosApp.Services;
using CafePosApp.ViewModels;

namespace CafePosApp.Views;

/// <summary>
/// Modal sheet hosting the catalogue forms (product / category / modifier group / ingredient / combo).
/// All behaviour lives in <see cref="CatalogFormViewModel"/> and its sub-ViewModels;
/// the page only pushes/pops itself, fills in the shared footers, and reports whether
/// anything was saved.
/// </summary>
public partial class CatalogFormsPage : ContentPage
{
    private readonly CatalogFormViewModel viewModel;
    private readonly IDialogService dialogs;
    private readonly TaskCompletionSource<bool> closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool saved;

    public CatalogFormsPage(CatalogFormViewModel viewModel, IDialogService dialogs)
    {
        InitializeComponent();
        BindingContext = this.viewModel = viewModel;
        this.dialogs = dialogs;
        viewModel.CloseRequested += OnCloseRequested;

        // One footer definition, five slots — see BuildFormFooter for why this is not a DataTemplate.
        ProductFooterHost.Children.Add(BuildFormFooter(viewModel.Product));
        CategoryFooterHost.Children.Add(BuildFormFooter(viewModel.Category));
        ModifierFooterHost.Children.Add(BuildFormFooter(viewModel.Modifier));
        IngredientFooterHost.Children.Add(BuildFormFooter(viewModel.Ingredient));
        ComboFooterHost.Children.Add(BuildFormFooter(viewModel.Combo));

        // The ScrollView used to ask for KeyboardDismissMode="WhileScrolling", which is an iOS
        // UIScrollView member no MAUI control exposes. Unfocus is the mechanism MAUI does have, and
        // the scroll is the trigger the attribute named, so the intent survives the rename.
        FormScroll.Scrolled += OnFormScrolled;
    }

    /// <summary>Prepares the requested form. Call before pushing the page.</summary>
    public Task OpenAsync(CatalogFormRequest request) => viewModel.OpenAsync(request);

    /// <summary>Completes when the sheet closes. True = the user saved something.</summary>
    public Task<bool> WaitForCloseAsync() => closed.Task;

    private async void OnCloseRequested(bool wasSaved)
    {
        saved = wasSaved;
        if (Navigation.ModalStack.Contains(this))
            await Navigation.PopModalAsync();
        closed.TrySetResult(saved);
    }

    /// <summary>
    /// The Android hardware back gesture and the iOS swipe both bypass the toolbar, so the form
    /// used to close with saved=false and no prompt — a half-typed product was thrown away with a
    /// single gesture. A dirty form now asks first; a pristine one still closes straight away so
    /// the common "opened it by mistake" case stays one tap.
    /// </summary>
    protected override bool OnBackButtonPressed()
    {
        if (!viewModel.IsDirty)
            return base.OnBackButtonPressed();

        // Returning true tells the platform the gesture was handled, so the sheet stays open
        // until the operator answers. The dialog is awaited off the gesture, which is why the
        // override itself must not be async void.
        _ = ConfirmDiscardAsync();
        return true;
    }

    private async Task ConfirmDiscardAsync()
    {
        var discard = await dialogs.ConfirmAsync(
            "Закрыть без сохранения?",
            "Изменения в этой форме не были сохранены.",
            "Закрыть",
            "Остаться");

        if (discard) CloseRequested(false);
    }

    private void CloseRequested(bool wasSaved)
    {
        saved = wasSaved;
        if (Navigation.ModalStack.Contains(this))
            _ = Navigation.PopModalAsync();
        closed.TrySetResult(saved);
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        // Also the escape hatch for the iOS swipe, which does not raise OnBackButtonPressed.
        // TrySetResult is a no-op if the sheet already closed through the normal path.
        closed.TrySetResult(saved);
    }

    /// <summary>
    /// Builds the footer shared by all five forms: the validation message over Save / Отмена.
    /// </summary>
    /// <remarks>
    /// This used to be a <c>DataTemplate</c> in the page's resources, instantiated through
    /// <c>&lt;ContentPresenter ContentTemplate="…"/&gt;</c> once per form. .NET MAUI 10 has no
    /// such property on any cross-platform control — not on <c>ContentView</c>, not on
    /// <c>ContentPresenter</c>, not on <c>ContentPage</c>; only <c>ShellContent</c> and the WinUI
    /// platform types expose it — so every one of those four elements aborted the process at
    /// inflation, which is the instant the catalogue FAB pushed this page. There is no declarative
    /// replacement: a <c>DataTemplateSelector</c> is only consumable by an ItemsView, and wrapping a
    /// single item in a CollectionView here would put a second scroller inside this page's
    /// ScrollView. So the footer is assembled in C#, exactly the way the catalogue action sheet
    /// assembles its rows, and the four <c>VerticalStackLayout</c> slots in the XAML are the
    /// containers it is added to. The keyed styles and the Danger colour are looked up by name
    /// through <see cref="ResourceStyles"/> because a control built in code cannot use
    /// <c>{StaticResource}</c>.
    /// </remarks>
    private static View BuildFormFooter(IFormFooterSource form)
    {
        // The form is the BindingContext of the whole footer, set on the root before anything is
        // added so every child inherits it. Setting it per control instead is the easy mistake
        // here: a typed SetBinding resolves its path against the target's own BindingContext, and a
        // control without one binds to nothing at all — silently. That produced a Save button with
        // neither a label nor a command, rather than an error.
        var footer = new Grid
        {
            RowDefinitions = [new(GridLength.Auto), new(GridLength.Auto)],
            RowSpacing = 6,
            Margin = new Thickness(0, 4, 0, 0),
            BindingContext = form,
        };

        var message = new Label
        {
            FontSize = 12,
            LineBreakMode = LineBreakMode.WordWrap,
            // ThemeColors.Resolve, not a bare TryGetColor("Danger"): this footer is built in code,
            // so it never participates in AppThemeBinding, and the light token alone left every
            // validation message light-red in dark mode. Danger reads 4.53:1 on the form's light
            // surface and DangerDark 8.36:1 on the dark one. ThemeColors falls back to the other
            // token if one is ever renamed, so this cannot throw.
            TextColor = ThemeColors.Resolve("Danger", "DangerDark"),
        };
        message.SetBinding(Label.TextProperty, static (IFormFooterSource f) => f.ValidationMessage);
        message.SetBinding(Label.IsVisibleProperty, static (IFormFooterSource f) => f.HasValidationMessage);

        var save = new Button { HorizontalOptions = LayoutOptions.Fill };
        save.SetBinding(Button.TextProperty, static (IFormFooterSource f) => f.SaveText);
        save.SetBinding(Button.CommandProperty, static (IFormFooterSource f) => f.SaveCommand);

        var cancel = new Button { Text = "Отмена", HorizontalOptions = LayoutOptions.Fill };
        ResourceStyles.TryApply(cancel, "SecondaryButton");
        cancel.SetBinding(Button.CommandProperty, static (IFormFooterSource f) => f.CancelCommand);

        // Columns both star, so the pair splits the form's width whatever the form.
        var buttons = new Grid
        {
            ColumnDefinitions = [new(GridLength.Star), new(GridLength.Star)],
            ColumnSpacing = 8,
        };
        buttons.Add(save);
        buttons.Add(cancel, column: 1);

        footer.Add(message);
        footer.Add(buttons, row: 1);
        return footer;
    }

    private void OnFormScrolled(object? sender, ScrolledEventArgs e) => DismissKeyboard();

    /// <summary>
    /// Dismisses the soft keyboard by unfocusing whichever form field currently holds focus.
    /// </summary>
    /// <remarks>
    /// This replaces <c>ScrollView.KeyboardDismissMode="WhileScrolling"</c> (an iOS
    /// <c>UIScrollView</c> member that no MAUI control exposes) and the <c>Page</c>-level
    /// <c>KeyboardAvoidingBehavior</c> that went with it (deleted from MAUI in .NET 9). Neither
    /// name exists in 10.0.101, so both were runtime aborts, not build errors.
    /// <para>
    /// The page cannot simply be asked whether it is focused: <c>Page.IsFocused</c> is true only for
    /// the page itself, not for a descendant <c>Entry</c>, so <c>Unfocus()</c> on the page is a
    /// no-op while a field is focused. The focused field is therefore found by walking the form —
    /// roughly twenty <c>InputView</c> nodes — with an early exit, so the common case of "nothing is
    /// focused" costs one pass and the walk stops at the first hit.
    /// </para>
    /// </remarks>
    private void DismissKeyboard()
    {
        foreach (var element in Descendants(this))
            if (element is InputView { IsFocused: true } field)
            {
                field.Unfocus();
                return;
            }
    }

    /// <summary>
    /// Walks the visual tree depth-first, so the keyboard is dismissed only by this sheet — a
    /// search field in some ancestor page is never touched by scrolling this form.
    /// </summary>
    private static IEnumerable<Element> Descendants(Element root)
    {
        var pending = new Stack<Element>();
        PushChildren(root, pending);

        while (pending.Count > 0)
        {
            if (!pending.TryPop(out var current)) yield break;
            PushChildren(current, pending);
            yield return current;
        }
    }

    private static void PushChildren(Element parent, Stack<Element> pending)
    {
        if (parent is not IVisualTreeElement visual) return;

        // Reversed, so the walk pops left-to-right and finds the topmost focused field first.
        var children = visual.GetVisualChildren();
        for (var i = children.Count - 1; i >= 0; i--)
            if (children[i] is Element child)
                pending.Push(child);
    }
}
