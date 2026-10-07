using System.Collections.ObjectModel;
using CafePos.Core.Common;
using CafePos.Core.Models;
using CafePos.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CafePos.Presentation.ViewModels;

/// <summary>
/// What the menu shows: the dishes, the bundle tiles, the category chips, and the filter over all three.
/// </summary>
/// <remarks>
/// <para>
/// The second Collaborator, and the one with the smallest dependency list the plan predicted for it —
/// two services and a clock. It owns four collections the XAML binds by name, so the shell proxies them
/// one line each.
/// </para>
/// <para>
/// <b>THE CLOCK IS A DEPENDENCY BECAUSE THE FILTER READS IT.</b> A dish served from 08:00 to 20:00
/// must be judged by the hour at the till, not the hour on the server. <see cref="TimeProvider"/>'s
/// <c>GetLocalNow</c> rather than <c>UtcNow</c> for the same reason: the shelf is in a local hour.
/// </para>
/// <para>
/// <b>WHAT DID NOT MOVE, AND WHY.</b> <c>LoadAsync</c> did not: it is the shell's because it also
/// restores the draft, re-raises the money and re-reads the clock. This class has
/// <see cref="LoadAsync"/> for the catalogue's half of it — the four queries and the two repairs — and
/// the shell calls that between its own steps. A collaborator that did the whole load would need the
/// draft service and the haptics to announce a restore, which is not what this owns.
/// </para>
/// </remarks>
public sealed class MenuCatalogue : ObservableObject
{
    private readonly ICatalogService catalog;
    private readonly IComboService combos;
    private readonly TimeProvider timeProvider;

    private Guid selectedFilterKey = MenuFilter.AllKey;

    public MenuCatalogue(ICatalogService catalog, IComboService combos, TimeProvider timeProvider)
    {
        this.catalog = catalog;
        this.combos = combos;
        this.timeProvider = timeProvider;
    }

    /// <summary>Every dish the till knows, before filtering.</summary>
    public ObservableCollection<Product> Products { get; } = [];

    /// <summary>The dishes on the grid: the selected category's, inside their time window.</summary>
    public ObservableCollection<Product> FilteredProducts { get; } = [];

    /// <summary>The category strip, «Все» first and «Комбо» last.</summary>
    public ObservableCollection<CategoryMenuItemViewModel> Categories { get; } = [];

    /// <summary>The bundle tiles, refreshed rather than rebuilt so they do not flicker on return.</summary>
    public ObservableCollection<MenuComboViewModel> Combos { get; } = [];

    /// <summary>
    /// Which chip is selected, as the catalogue knows it.
    /// </summary>
    /// <remarks>
    /// The key rather than the chip, because the strip is rebuilt on every return to the tab and a chip
    /// reference would not survive that. The two sentinels live on <see cref="MenuFilter"/>.
    /// </remarks>
    public Guid SelectedFilterKey
    {
        get => selectedFilterKey;
        private set
        {
            if (selectedFilterKey == value) return;
            selectedFilterKey = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SelectedCategory));
            OnPropertyChanged(nameof(IsBundlesOnly));
        }
    }

    /// <summary>The chosen category, or <c>null</c> for everything.</summary>
    public Category? SelectedCategory =>
        Categories.FirstOrDefault(chip => chip.Key == SelectedFilterKey)?.Category;

    /// <summary>True for the bundles-only filter, which empties the grid rather than narrowing it.</summary>
    public bool IsBundlesOnly => MenuFilter.IsBundlesOnly(SelectedFilterKey);

    /// <summary>
    /// Reads the catalogue: dishes, categories and bundle tiles, then repairs the selection and filters.
    /// </summary>
    /// <remarks>
    /// The tiles are refreshed through <c>SyncWith</c>'s update callback rather than re-created, so the
    /// row does not flicker on every return to the tab — the same reason the chips are updated and not
    /// rebuilt.
    /// </remarks>
    public async Task LoadAsync()
    {
        var products = await catalog.GetProductsAsync();
        var categories = await catalog.GetCategoriesAsync();

        Products.SyncWith(products, product => product.Id);

        var templates = await combos.GetCombosAsync();
        Combos.SyncWith(
            [.. templates.Select(BuildComboTile)],
            row => row.Id,
            (current, incoming) => current.Refresh(incoming));

        // The "Все" chip is the first element of the strip, so the whole row scrolls as one list, and
        // "Комбо" is the last: it is a filter over the other kind of content rather than a section among
        // these, so it belongs at the end where it reads as a switch.
        Categories.SyncWith(
            [CategoryMenuItemViewModel.CreateAll(),
             .. categories.Select((category, index) => new CategoryMenuItemViewModel(category, index)),
             CategoryMenuItemViewModel.CreateCombos()],
            item => item.Key);

        // The selected section may have been deleted while the page was closed. MenuFilter.Repair is
        // where that rule lives, and it is a rule about the catalogue rather than about a screen.
        SelectedFilterKey = MenuFilter.Repair(SelectedFilterKey, [.. Categories.Select(item => item.Key)]);

        HighlightSelectedChip();
        ApplyFilters();
    }

    /// <summary>Chooses a chip, and refilters.</summary>
    public void SelectCategory(CategoryMenuItemViewModel? item) =>
        SelectedFilterKey = item?.Key ?? MenuFilter.AllKey;

    /// <summary>Keeps the chip strip in sync with <see cref="SelectedFilterKey"/>.</summary>
    public void HighlightSelectedChip()
    {
        foreach (var chip in Categories)
            chip.IsSelected = chip.Key == SelectedFilterKey;
    }

    /// <summary>
    /// Rebuilds <see cref="FilteredProducts"/> from the selected category and the time window.
    /// </summary>
    /// <remarks>
    /// Diff based filtering: no <c>Clear()</c>, so the grid does not flicker or lose its scroll. There
    /// is no search on this page — the owner's call, see the row-structure comment in
    /// <c>Views/MenuPage.xaml</c> — so nothing else feeds this list.
    /// </remarks>
    public void ApplyFilters()
    {
        var visible = IsBundlesOnly
            ? []
            : MenuFilter.Apply(Products, SelectedCategory, timeProvider.GetLocalNow().Hour);

        FilteredProducts.SyncWith(visible, product => product.Id);
    }

    /// <summary>
    /// Re-raises the bundle tiles' formatted amounts, after the selected currency may have changed.
    /// </summary>
    /// <remarks>
    /// The tiles are the one row on this page whose money is not produced by a converter — a tile knows
    /// its price from the composition, not from a <see cref="Product"/> — so nothing else re-raises them
    /// when the operator switches currency on the Settings tab.
    /// </remarks>
    public void RefreshMoneyText()
    {
        foreach (var combo in Combos) combo.RefreshMoneyText();
    }

    /// <summary>
    /// Reads a catalogue bundle against what is on the shelf right now, and off the same slots the tile
    /// names in its composition line.
    /// </summary>
    private static MenuComboViewModel BuildComboTile(Combo template)
    {
        var plan = BundlePlan.Describe(template);
        return new MenuComboViewModel(
            template,
            template.PriceKopecks,
            plan.BlockedDishName,
            TextFormat.Money(Money.FromKopecks(template.PriceKopecks)),
            // Off the SAME components GetCombosAsync already loaded, not a query per tile: the names
            // were in hand to compute the price two lines above, and a second read would be N+1 on the
            // one screen the cashier looks at most. See ComboComposition.
            ComboComposition.Summarise(template.Components));
    }
}