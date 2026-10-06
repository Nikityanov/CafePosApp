using CafePos.Core.Common;
using CafePos.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CafePos.Presentation.ViewModels;
/// <summary>
/// One editable slot of a bundle: the dish, how many of it, what it is charged, and what replaces it
/// when it runs out.
/// </summary>
/// <remarks>
/// A ViewModel rather than the <see cref="ComboComponent"/> itself, and the reason is the price mode:
/// the entity stores <c>long?</c> and three distinct values, which no control can present honestly.
/// Two pickers also have to hold state per row, and a shared <c>Picker.ItemsSource</c> cannot also be
/// the row's own selection.
/// <para>
/// Every field re-reads <see cref="UnitKopecks"/> through <see cref="ComboPricing.ResolveUnitKopecks"/>
/// rather than storing a resolved number, which is the same rule the sale side applies — so what the
/// manager sees here is exactly what the till will charge, including across a substitution.
/// </para>
/// </remarks>
public sealed class ComboSlotRow : ObservableObject
{
    private readonly Action changed;

    private CatalogOption<Product>? selectedProductOption;
    private CatalogOption<Product>? selectedSubstituteOption;
    private ComboPriceModeOption? selectedPriceModeOption;
    private ComboPriceMode priceMode = ComboPriceMode.DishPrice;
    private string quantityText = "1";
    private string fixedPriceText = string.Empty;

    /// <summary>Last valid multiplicity. Held while the field is mid-typing so the live total keeps meaning something.</summary>
    private int quantityPerUnit = 1;
    private bool isQuantityValid = true;

    /// <summary>Last valid fixed price in kopecks, for the same reason as <see cref="quantityPerUnit"/>.</summary>
    private long fixedPriceKopecks;
    private bool isFixedPriceValid = true;

    /// <summary>The exact value that will be written to <see cref="ComboComponent.ComponentPriceKopecks"/>.</summary>
    private long? componentPriceKopecks;

    /// <param name="id">
    /// The slot's identity. Preserved across an edit so the save matches the row it came from instead
    /// of deleting it and inserting a new one; <see cref="Guid.NewGuid"/> for a slot added here.
    /// </param>
    /// <param name="changed">Called after anything that can move the bundle's price.</param>
    public ComboSlotRow(
        Guid id,
        IEnumerable<CatalogOption<Product>> productOptions,
        IEnumerable<CatalogOption<Product>> substituteOptions,
        IEnumerable<ComboPriceModeOption> priceModes,
        Action changed)
    {
        Id = id;
        ProductOptions = [.. productOptions];
        SubstituteOptions = [.. substituteOptions];
        PriceModes = [.. priceModes];
        this.changed = changed;
    }

    /// <summary>The dish's name for the row, or a prompt while the slot is still empty.</summary>
    public string ProductName => Product?.Name ?? "Выберите блюдо";

    /// <summary>
    /// What this slot adds to the à la carte reference, formatted. A bundle takes a whole number of a
    /// dish, so this is the only figure the row needs — the operator is checking the total above, not
    /// multiplying out loud.
    /// </summary>
    public string LineTotalText => HasProduct
        ? $"{quantityPerUnit} × {TextFormat.Money(Money.FromKopecks(UnitKopecks))}"
        : string.Empty;

    /// <summary>Placeholder for the "своя цена" field, naming the selected currency's unit.</summary>
    public string FixedPricePlaceholder => $"Цена, {Currencies.Default.MinorUnitName}";

    public Guid Id { get; }

    public IReadOnlyList<CatalogOption<Product>> ProductOptions { get; }
    public IReadOnlyList<CatalogOption<Product>> SubstituteOptions { get; }
    public IReadOnlyList<ComboPriceModeOption> PriceModes { get; }

    // ── Dish ──────────────────────────────────────────────────────────────────────────────────

    public CatalogOption<Product>? SelectedProductOption
    {
        get => selectedProductOption;
        set { if (SetProperty(ref selectedProductOption, value)) Refresh(); }
    }

    public Product? Product => SelectedProductOption?.Value;
    public Guid? ProductId => Product?.Id;
    public bool HasProduct => Product is not null;

    // ── Multiplicity ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Nudges the multiplicity by whole units, for the cart's stepper.
    /// </summary>
    /// <remarks>
    /// Goes through <see cref="QuantityText"/> rather than writing
    /// <see cref="QuantityPerUnit"/> directly. That is the whole point of the indirection: the setter is
    /// where validation, the warning text and the recomputed totals live, so a stepper that bypassed it
    /// would be a second place where quantity changes — and it would be the one with no guard. A bundle
    /// takes a whole number of a dish, never a fraction and never zero, which is why the floor is 1 and
    /// why <c>−</c> at 1 is refused rather than clamped: silently turning "−" into a no-op leaves the
    /// operator pressing a button that does nothing, and one that reaches 0 would multiply the component
    /// out of the bundle's own total.
    /// </remarks>
    public void StepQuantity(int delta)
    {
        var current = int.TryParse(QuantityText.Trim(), out var parsed) && parsed >= 1 ? parsed : 1;
        var next = current + delta;
        if (next < 1) return;

        QuantityText = next.ToString();
    }

    public string QuantityText
    {
        get => quantityText;
        set
        {
            if (!SetProperty(ref quantityText, value ?? string.Empty)) return;

            // Below 1 is refused by the domain, and 0 would silently multiply nothing away, so an
            // unusable entry holds the LAST VALID value instead of contributing 0 to the total. The
            // row says so in its warning and the save is blocked; a live figure that silently
            // dropped to a third would be the worse of the two.
            if (int.TryParse(quantityText.Trim(), out var parsed) && parsed >= 1)
            {
                if (!isQuantityValid)
                {
                    isQuantityValid = true;
                    OnPropertyChanged(nameof(IsQuantityValid));
                }

                if (parsed != quantityPerUnit)
                {
                    quantityPerUnit = parsed;
                    OnPropertyChanged(nameof(QuantityPerUnit));
                }
            }
            else if (isQuantityValid)
            {
                isQuantityValid = false;
                OnPropertyChanged(nameof(IsQuantityValid));
            }

            Refresh();
        }
    }

    public int QuantityPerUnit => quantityPerUnit;

    public bool IsQuantityValid => isQuantityValid;

    // ── What the slot is charged ──────────────────────────────────────────────────────────────

    public ComboPriceModeOption? SelectedPriceModeOption
    {
        get => selectedPriceModeOption;
        set
        {
            if (!SetProperty(ref selectedPriceModeOption, value)) return;
            ApplyPriceMode(value?.Mode ?? ComboPriceMode.DishPrice);
        }
    }

    public ComboPriceMode PriceMode => priceMode;

    /// <summary>Only a fixed slot has a price to type; the other two modes own their own figure.</summary>
    public bool IsFixedPrice => priceMode == ComboPriceMode.Fixed;

    public bool IsFixedPriceValid => isFixedPriceValid;

    public string FixedPriceText
    {
        get => fixedPriceText;
        set
        {
            if (!SetProperty(ref fixedPriceText, value ?? string.Empty)) return;
            RecomputeSlotPrice();
            Refresh();
        }
    }

    /// <summary>What will be stored. Null takes the dish price, 0 is free, a number is this price instead.</summary>
    public long? ComponentPriceKopecks => componentPriceKopecks;

    // ── Substitute ────────────────────────────────────────────────────────────────────────────

    public CatalogOption<Product>? SelectedSubstituteOption
    {
        get => selectedSubstituteOption;
        set { if (SetProperty(ref selectedSubstituteOption, value)) Refresh(); }
    }

    public Guid? SubstituteProductId => SelectedSubstituteOption?.Value?.Id;
    public bool HasSubstitute => SubstituteProductId is not null;

    // ── Derived figures ───────────────────────────────────────────────────────────────────────

    /// <summary>
    /// What one of this dish contributes to the à la carte reference: the DISH's own price, always.
    /// </summary>
    /// <remarks>
    /// This used to be the slot's own price when it stated one, otherwise the dish's. The form no
    /// longer exposes a per-slot price, so that override became invisible while it still drove the
    /// arithmetic — the worst of both. The screen printed «Блюдо стоит 160.00 ₿» while the
    /// «по отдельноcти» figure was summed from a stored 120.00 ₿, and the discount the customer is
    /// quoted was computed from the invisible one. A number the operator cannot see and cannot change
    /// must not decide the saving, so the override is out of the calculation entirely.
    /// <para>
    /// THE SAVED VALUE IS STILL WRITTEN. <c>componentPriceKopecks</c> is preserved and BuildSlots
    /// still saves it, so nothing is destroyed and reverting this is a one-line change. It is dead
    /// weight, not a deleted field: the column still exists and the still-stored values are now
    /// ignored, which is the honest way to end it — dropping them is a migration nobody asked for.
    /// </para>
    /// <para>
    /// The bundle's CHARGE is unaffected either way: that is <c>Combo.PriceKopecks</c> and never came
    /// from here. This figure only ever measured the bundle against buying its parts separately.
    /// </para>
    /// </remarks>
    public long UnitKopecks => Product?.PriceKopecks ?? 0;

    /// <summary>What the slot contributes to one unit of the bundle.</summary>
    public long LineKopecks => (long)quantityPerUnit * UnitKopecks;

    /// <summary>The dish's own price, shown next to the slot price so the choice is a visible comparison.</summary>
    public string ProductPriceText => Product is { } product
        ? $"Блюдо стоит {TextFormat.Money(product.Price)}"
        : "Блюдо не выбрано";

    /// <summary>The chosen mode in words — the slot's price is derived, so it is stated, not typed.</summary>
    public string PriceModeSummary => priceMode switch
    {
        ComboPriceMode.Free => "Бесплатно",
        ComboPriceMode.Fixed => $"Своя цена: {TextFormat.Money(Money.FromKopecks(UnitKopecks))}",
        _ => Product is { } product ? $"Цена блюда: {TextFormat.Money(product.Price)}" : "Цена блюда"
    };

    /// <summary>The slot's own contribution, so the total above can be checked by eye.</summary>
    public string LineSummary =>
        $"{quantityPerUnit} × {TextFormat.Money(Money.FromKopecks(UnitKopecks))}"
        + $" = {TextFormat.Money(Money.FromKopecks(LineKopecks))}";

    /// <summary>The replacement, or an empty string when none is set. D365's rule: a dearer replacement reprices the bundle.</summary>
    public string SubstituteText => SelectedSubstituteOption?.Value is { } substitute
        ? $"Замена: {substitute.Name}"
        : "Замена не настроена — без блюда комбо не продать";

    /// <summary>
    /// What is wrong with this row, in words. Empty when nothing is. The save is refused on the same
    /// conditions, so this is a warning that appears where the mistake is rather than at the button.
    /// </summary>
    public string WarningText
    {
        get
        {
            var problems = new List<string>(2);
            if (!HasProduct) problems.Add("выберите блюдо");
            if (!isQuantityValid) problems.Add("количество должно быть целым числом не меньше 1");
            if (priceMode == ComboPriceMode.Fixed && !isFixedPriceValid) problems.Add("своя цена должна быть числом не меньше нуля");
            return problems.Count == 0 ? string.Empty : string.Join("; ", problems);
        }
    }

    /// <summary>
    /// Loads a saved slot into a fresh row. Assigns the backing fields and refreshes once, rather
    /// than going through the setters, so opening a bundle does not re-announce every member N times
    /// while nothing is bound to the row yet.
    /// </summary>
    public void Initialise(
        Guid? productId,
        Guid? substituteProductId,
        ComboPriceMode mode,
        int quantity,
        long? componentPrice)
    {
        selectedProductOption = ProductOptions.FirstOrDefault(option => option.Value?.Id == productId);
        selectedSubstituteOption = substituteProductId is null
            ? SubstituteOptions.FirstOrDefault(option => option.IsNone)
            : SubstituteOptions.FirstOrDefault(option => option.Value?.Id == substituteProductId);

        priceMode = mode;
        selectedPriceModeOption = PriceModes.FirstOrDefault(option => option.Mode == mode);
        OnPropertyChanged(nameof(PriceMode));
        OnPropertyChanged(nameof(IsFixedPrice));
        OnPropertyChanged(nameof(SelectedPriceModeOption));

        quantityPerUnit = Math.Max(1, quantity);
        quantityText = quantityPerUnit.ToString();
        OnPropertyChanged(nameof(QuantityPerUnit));
        OnPropertyChanged(nameof(QuantityText));

        fixedPriceKopecks = componentPrice ?? 0;
        // Shown even in the other two modes, so switching to "своя цена" does not blank the field the
        // manager is about to edit.
        fixedPriceText = Money.FromKopecks(fixedPriceKopecks).ToString("0.##");
        OnPropertyChanged(nameof(FixedPriceText));

        RecomputeSlotPrice();
        Refresh();
    }

    private void ApplyPriceMode(ComboPriceMode mode)
    {
        if (priceMode == mode) return;
        priceMode = mode;
        OnPropertyChanged(nameof(PriceMode));
        OnPropertyChanged(nameof(IsFixedPrice));

        // Seeding the fixed field with the dish's price is a convenience, not a decision: leaving it
        // untouched behaves exactly like «цена блюда», so nothing is silently written by switching
        // modes and looking again.
        if (mode == ComboPriceMode.Fixed && fixedPriceText.Length == 0 && Product is { } product)
            SetProperty(ref fixedPriceText, product.Price.ToString("0.##"), nameof(FixedPriceText));

        RecomputeSlotPrice();
        Refresh();
    }

    private void RecomputeSlotPrice()
    {
        var valid = true;
        if (priceMode == ComboPriceMode.Fixed)
        {
            if (TextFormat.TryParseDecimal(fixedPriceText.Trim(), out var amount) && amount >= 0)
                fixedPriceKopecks = Money.ToKopecks(amount);
            else
                valid = false;
        }

        if (valid != isFixedPriceValid)
        {
            isFixedPriceValid = valid;
            OnPropertyChanged(nameof(IsFixedPriceValid));
        }

        var price = priceMode switch
        {
            ComboPriceMode.DishPrice => (long?)null,
            ComboPriceMode.Free => 0L,
            _ => fixedPriceKopecks
        };

        if (price != componentPriceKopecks)
        {
            componentPriceKopecks = price;
            OnPropertyChanged(nameof(ComponentPriceKopecks));
        }
    }

    private void Refresh()
    {
        OnPropertyChanged(nameof(Product));
        OnPropertyChanged(nameof(ProductId));
        OnPropertyChanged(nameof(HasProduct));
        OnPropertyChanged(nameof(SubstituteProductId));
        OnPropertyChanged(nameof(HasSubstitute));
        OnPropertyChanged(nameof(UnitKopecks));
        OnPropertyChanged(nameof(LineKopecks));
        OnPropertyChanged(nameof(ProductPriceText));
        OnPropertyChanged(nameof(PriceModeSummary));
        OnPropertyChanged(nameof(LineSummary));
        OnPropertyChanged(nameof(SubstituteText));
        OnPropertyChanged(nameof(WarningText));

        // The cart-shaped row's own two figures. Refresh is the right place rather than each setter,
        // because both depend on the dish AND on the quantity: announcing them from the product setter
        // alone would leave the line total stale after a stepper press, which is the change an operator
        // makes most often.
        OnPropertyChanged(nameof(ProductName));
        OnPropertyChanged(nameof(LineTotalText));

        changed();
    }
}
