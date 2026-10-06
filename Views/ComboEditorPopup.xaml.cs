using CafePos.Core.Common;
using CafePosApp.Controls;
using CafePosApp.Diagnostics;
using CafePosApp.Services;
using CafePos.Presentation.Services;
using CommunityToolkit.Maui.Views;
using Microsoft.Maui.Controls.Xaml;
using Microsoft.Maui.Devices;

namespace CafePosApp.Views;

/// <summary>
/// What the composition sheet closed with. Never null: a dismissal is a <c>null</c> result from the
/// sheet, and an empty slot list is a bundle with no components — which is refused, not confirmed.
/// </summary>
public sealed record ComboEditorResult(IReadOnlyList<ComboSlotChoice> Slots);

/// <summary>
/// The one composition sheet: a catalogue bundle opens it pre-filled and editable, and a custom build
/// opens the same sheet empty.
/// </summary>
/// <remarks>
/// <b>ONE MECHANIC, BOTH CASES.</b> Merchandising research puts the "combos stopped showing in
/// reports" bug exactly where a shop grows a second editor — a read-only view for catalogue bundles
/// and a builder for custom ones — because the two then disagree about what a bundle is, and only one
/// of them feeds the sale. So this sheet has one row shape, one running total and one confirm button,
/// and a custom build is <see cref="ClearButton"/> pressed on a catalogue bundle.
/// <para>
/// The dish set it offers comes from the caller, and that is deliberate: it is the bundle's own
/// catalogue slots, resolved against what is on the shelf right now. A dish outside that set is
/// refused at sale by <c>ComboService.ResolveOne</c>, naming itself, so offering it here would be
/// offering a choice that cannot be sold — the same "control that can only fail" shape this codebase
/// refuses elsewhere.
/// </para>
/// <para>
/// The running total is the bundle's OWN price when the caller passed one, or the à la carte
/// <see cref="ComboPricing.ReferenceKopecks"/> over the slots when it did not. The price that is
/// finally charged is recomputed from the catalogue at checkout; this is a quote, and the sheet is
/// where the cashier sees it.
/// </para>
/// </remarks>
[XamlCompilation(XamlCompilationOptions.Compile)]
public partial class ComboEditorPopup : Popup<ComboEditorResult?>
{
    /// <summary>
    /// A slot being edited. Mutable rather than a record: the sheet rebuilds rows after every change,
    /// so the authoritative copy is this list and the labels are re-read from it.
    /// </summary>
    private sealed class Slot
    {
        public required ComboSlotOption Option { get; init; }
        public int QuantityPerUnit { get; set; } = 1;
    }

    private readonly List<Slot> slots = [];
    private readonly IReadOnlyList<ComboSlotOption> options;
    private readonly long priceKopecks;

    public ComboEditorPopup(ComboEditorRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        InitializeComponent();
        options = request.Options;
        priceKopecks = request.PriceKopecks;
        SizeToWindow();
        SizeChanged += (_, _) => SizeToWindow();

        TitleLabel.Text = request.Title;
        TranslationY = 1000;

        // Pre-filled or empty, by the same two lines. Nothing here knows which case it is.
        foreach (var choice in request.Selection)
        {
            var option = options.FirstOrDefault(candidate => candidate.ProductId == choice.ProductId);
            if (option is null) continue;
            slots.Add(new Slot
            {
                Option = option,
                QuantityPerUnit = Math.Max(1, choice.QuantityPerUnit)
            });
        }

        Opened += (_, _) =>
        {
            SizeToWindow();
            Refresh();
            _ = this.TranslateToAsync(0, 0, 300, Easing.CubicOut);
        };
    }

    /// <summary>
    /// Rebuilds both row lists and the totals.
    /// </summary>
    /// <remarks>
    /// Rebuilt rather than bound, and the reason is the same one ModifierPickerPopup documents: the
    /// number of rows is whatever the cashier has in the sheet, so there is no fixed set of bindings
    /// to write. Clearing the layout is safe here — these are Buttons in a stack, not a
    /// <c>BindableLayout</c>, and <see cref="ObservableCollectionSync"/>'s Replace trap does not apply.
    /// </remarks>
    private void Refresh()
    {
        SlotsLayout.Clear();
        OptionsLayout.Clear();

        foreach (var slot in slots) SlotsLayout.Add(BuildSlotRow(slot));

        // "Empty" is a legitimate state on its own — it is the custom build mid-construction — so it
        // is labelled rather than left blank. A sheet with nothing in it and nothing saying so reads
        // as a sheet that failed to load.
        EmptySlotsLabel.IsVisible = slots.Count == 0;
        ClearButton.IsVisible = slots.Count > 0;

        // One option per distinct dish, in the order the caller gave them, so the sheet reads in the
        // bundle's own order rather than in the order it happens to be built.
        foreach (var option in options.DistinctBy(candidate => candidate.ProductId))
        {
            var button = new Button
            {
                Text = $"+ {option.Label}",
                HorizontalOptions = LayoutOptions.Fill
            };
            ResourceStyles.TryApply(button, "SheetAction");
            var captured = option;
            button.Clicked += (_, _) => AddSlot(captured);
            OptionsLayout.Add(button);
        }

        // THE BUNDLE'S OWN PRICE is the main figure — what the till charges. The à la carte sum of
        // the slots is the reference it is measured against, shown as the comparison. Under the old
        // model the sum WAS the price; under the current one the price is a number in the bundle's
        // card and the sum is only what the discount is measured against.
        //
        // When the caller did not pass a price (a custom build from scratch), the sum is shown
        // alone — there is no own price to display yet.
        var reference = ComboPricing.ReferenceKopecks(
            slots.Select(slot => new ComboComponentPrice(slot.QuantityPerUnit, slot.Option.UnitKopecks)));

        if (priceKopecks > 0)
        {
            TotalLabel.Text = TextFormat.Money(Money.FromKopecks(priceKopecks));

            // The discount, in words — cheaper is a discount, dearer is a surcharge (labelled
            // honestly, never as a negative discount), and a zero reference has no percentage at
            // all. This is the same computation the catalogue list and the form use.
            var percent = ComboPricing.DiscountPercent(reference, priceKopecks);
            if (percent is not null)
            {
                var formatted = percent.Value.ToString("0.##", System.Globalization.CultureInfo.CurrentCulture);
                var relation = ComboPricing.Compare(reference, priceKopecks);
                ReferenceLabel.IsVisible = true;
                ReferenceLabel.Text = relation switch
                {
                    ComboPriceRelation.Cheaper => $"Скидка {formatted}% · по отдельности {TextFormat.Money(Money.FromKopecks(reference))}",
                    ComboPriceRelation.Dearer => $"Наценка {formatted}% · по отдельности {TextFormat.Money(Money.FromKopecks(reference))}",
                    _ => $"Цена равна сумме компонентов · по отдельности {TextFormat.Money(Money.FromKopecks(reference))}"
                };
            }
            else
            {
                ReferenceLabel.IsVisible = true;
                ReferenceLabel.Text = $"По отдельности {TextFormat.Money(Money.FromKopecks(reference))} — сравнивать не с чем";
            }
        }
        else
        {
            TotalLabel.Text = $"Итого: {TextFormat.Money(Money.FromKopecks(reference))}";
            ReferenceLabel.IsVisible = false;
        }

        // Confirming an empty composition is refused here rather than passed on as an empty sale. The
        // domain refuses it too (a bundle with no slots prices at zero, which is a free meal rather
        // than a bundle), but a sheet that lets the operator press the button to reach that error has
        // already spent their attention on it.
        ConfirmButton.IsEnabled = slots.Count > 0;
    }

    private View BuildSlotRow(Slot slot)
    {
        var grid = new Grid
        {
            ColumnDefinitions =
            {
                new(GridLength.Star),
                new(GridLength.Auto),
                new(GridLength.Auto),
                new(GridLength.Auto),
                new(GridLength.Auto)
            },
            ColumnSpacing = 6,
            MinimumHeightRequest = 48
        };

        var name = new Label
        {
            Text = slot.Option.Label,
            FontSize = 13,
            LineBreakMode = LineBreakMode.TailTruncation,
            VerticalTextAlignment = TextAlignment.Center
        };
        grid.Add(name);

        // The three controls wear the app's RowAction shape rather than the 48dp StepperButton: four
        // of those across a 411dp sheet would leave the dish's name about 40dp, and a composition the
        // cashier cannot read is a composition they cannot check. 44dp is still the touch minimum.
        var minus = Control("−", "Убрать одну штуку");
        minus.Clicked += (_, _) => ChangeQuantity(slot, -1);
        grid.Add(minus, 1);

        var quantity = new Label
        {
            Text = slot.QuantityPerUnit.ToString(),
            WidthRequest = 28,
            FontSize = 17,
            FontAttributes = FontAttributes.Bold,
            HorizontalTextAlignment = TextAlignment.Center,
            VerticalTextAlignment = TextAlignment.Center
        };
        SemanticProperties.SetDescription(quantity, "Сколько входит в один набор");
        grid.Add(quantity, 2);

        var plus = Control("+", "Добавить ещё одну");
        plus.Clicked += (_, _) => ChangeQuantity(slot, +1);
        grid.Add(plus, 3);

        var remove = Control("✕", $"Убрать {slot.Option.Label} из состава");
        remove.Clicked += (_, _) =>
        {
            slots.Remove(slot);
            Refresh();
        };
        grid.Add(remove, 4);

        return grid;
    }

    /// <summary>
    /// A fixed-width control built in code: <see cref="Button"/> has no content property, so the
    /// glyph is the text and the size comes from here, not from a style that a code-built control
    /// cannot reach through markup.
    /// </summary>
    private static Button Control(string glyph, string description)
    {
        var button = new Button
        {
            Text = glyph,
            WidthRequest = 44,
            HeightRequest = 44,
            MinimumWidthRequest = 44,
            MinimumHeightRequest = 44,
            Padding = 0,
            FontSize = 16,
            FontAttributes = FontAttributes.Bold
        };
        ResourceStyles.TryApply(button, "RowAction");
        SemanticProperties.SetDescription(button, description);
        return button;
    }

    private void AddSlot(ComboSlotOption option)
    {
        // Adding a dish that is already a slot tops the slot up rather than creating a second one:
        // two rows for the same dish with the prices split across them would print as two lines and
        // read as a different composition from the one the catalogue states.
        var existing = slots.FirstOrDefault(slot => slot.Option.ProductId == option.ProductId);
        if (existing is not null)
        {
            existing.QuantityPerUnit++;
            Refresh();
            return;
        }

        slots.Add(new Slot { Option = option, QuantityPerUnit = 1 });
        Refresh();
    }

    private void ChangeQuantity(Slot slot, int delta)
    {
        // Never zero. A slot with no copies of its dish is not a slot with a zero in it, and a
        // multiplicity of 0 is exactly what ComboService.SaveComboAsync refuses.
        slot.QuantityPerUnit = Math.Max(1, slot.QuantityPerUnit + delta);
        Refresh();
    }

    private void OnClearClicked(object? sender, EventArgs e)
    {
        slots.Clear();
        Refresh();
    }

    private void OnConfirmClicked(object? sender, EventArgs e) =>
        _ = CloseAsync(new ComboEditorResult(slots
            .Select(slot => new ComboSlotChoice(slot.Option.ProductId, slot.QuantityPerUnit))
            .ToList()));

    private async void OnCancelClicked(object? sender, EventArgs e) => await CloseAsync(null);

    /// <summary>
    /// Matches the sheet to the window it is shown over, and lifts its content above the system
    /// navigation bar. Copied from <see cref="PaymentSheetPopup.SizeToWindow"/> — see its remarks.
    /// </summary>
    private void SizeToWindow()
    {
        try
        {
            var window = Application.Current?.Windows.FirstOrDefault();
            var width = window?.Width ?? 0;
            if (width <= 0) width = this.Width;
            if (width <= 0) width = DeviceDisplay.MainDisplayInfo.Width / DeviceDisplay.MainDisplayInfo.Density;
            if (width > 0) WidthRequest = width;

            var height = window?.Height ?? 0;
            if (height > 0 && SheetScroll is not null) SheetScroll.MaximumHeightRequest = height * 0.9;

            if (SheetContent is not null) SheetContent.Padding = new Thickness(16, 8, 16, 16 + GetBottomInset());
        }
        catch (Exception exception)
        {
            AppLog.Exception("ComboEditorPopup.SizeToWindow", exception);
        }
    }

    /// <summary>
    /// The height of the system navigation bar in device-independent pixels, or 0 when there is none.
    /// Android-only; the other platforms either have no bottom bar or inset their content themselves.
    /// </summary>
    private static double GetBottomInset()
    {
        try
        {
#if ANDROID
            var resources = Platform.CurrentActivity?.Resources;
            if (resources is not null)
            {
                var resourceId = resources.GetIdentifier("navigation_bar_height", "dimen", "android");
                if (resourceId > 0)
                {
                    var bottomPx = resources.GetDimensionPixelSize(resourceId);
                    var metrics = resources.DisplayMetrics;
                    if (bottomPx > 0 && metrics is not null) return bottomPx / metrics.Density;
                }
            }
#endif
            return 0;
        }
        catch
        {
            return 0;
        }
    }
}
