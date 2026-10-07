using CafePos.Core.Common;
using CafePos.Core.Data;
using CafePos.Core.Errors;
using CafePos.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CafePos.Core.Services;

/// <summary>Bundles in the catalogue, and the one place a requested composition is turned into one that may be sold.</summary>
/// <remarks>Почему так — `docs/decisions/combos.md`</remarks>

public sealed class ComboService(
    IDbContextFactory<AppDbContext> factory,
    ILogger<ComboService> logger) : IComboService
{
    // ─── Reads ───

    public async Task<List<Combo>> GetCombosAsync(bool includeDeleted = false, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var combos = await LoadAsync(db, includeDeleted, cancellationToken);

        // Sorted in memory like every other ordered catalogue list in this codebase: SQLite cannot ORDER BY a DateTimeOffset, and the ordering a catalogue scree…
        // Почему так — `docs/decisions/combos.md`

        return combos
            .OrderBy(combo => combo.SortOrder)
            .ThenBy(combo => combo.Name, StringComparer.CurrentCulture)
            .ToList();
    }

    public async Task<Combo?> GetComboAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var combos = await LoadAsync(db, includeDeleted: true, cancellationToken);
        return combos.FirstOrDefault(combo => combo.Id == id);
    }

    private static Task<List<Combo>> LoadAsync(AppDbContext db, bool includeDeleted, CancellationToken cancellationToken)
    {
        var query = db.Combos.AsNoTracking()
            .Include(combo => combo.Components).ThenInclude(component => component.Product)
            .Include(combo => combo.Components).ThenInclude(component => component.SubstituteProduct)
            .AsSplitQuery()
            .Where(combo => includeDeleted || !combo.IsDeleted);

        return query.ToListAsync(cancellationToken);
    }

    // ─── Writes ───

    public async Task SaveComboAsync(Combo combo, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(combo);

        if (string.IsNullOrWhiteSpace(combo.Name)) throw new ValidationFailureException("Укажите название комбо.");

        /// <summary>THE PRICE IS REQUIRED AND MUST BE POSITIVE.</summary>
        /// <remarks>Почему так — `docs/decisions/combos.md`</remarks>

        if (combo.PriceKopecks <= 0)
            throw new ValidationFailureException("Укажите цену комбо: товар по нулевой цене — это ошибка, а не выгодное предложение.");
        if (combo.Components.Count == 0) throw new ValidationFailureException("Добавьте хотя бы один компонент в комбо.");

        foreach (var component in combo.Components)
        {
            if (component.QuantityPerUnit < 1)
                throw new ValidationFailureException("Количество компонента должно быть не меньше 1.");
            if (component.ComponentPriceKopecks is < 0)
                throw new ValidationFailureException("Цена компонента не может быть отрицательной.");
        }

        await using var db = await factory.CreateDbContextAsync(cancellationToken);

        await ValidateReferencesAsync(db, combo, cancellationToken);

        var existing = await db.Combos
            .Include(current => current.Components)
            .FirstOrDefaultAsync(current => current.Id == combo.Id, cancellationToken);

        if (existing is null)
        {
            foreach (var component in combo.Components)
            {
                component.Id = component.Id == Guid.Empty ? Guid.NewGuid() : component.Id;
                component.ComboId = combo.Id;
            }

            db.Combos.Add(combo);
        }
        else
        {
            existing.Name = combo.Name.Trim();
            existing.SortOrder = combo.SortOrder;
            // Copied, never recomputed. This is the number accounting decided, and deriving it here
            // from the slots is precisely the model this feature removed.
            existing.PriceKopecks = combo.PriceKopecks;
            SyncComponents(db, existing, combo.Components);
        }

        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation(
            "Combo {ComboId} «{Name}» saved at {Price} with {Count} components",
            combo.Id, combo.Name, Money.FromKopecks(combo.PriceKopecks), combo.Components.Count);
    }

    /// <summary>Refuses a slot that points at a dish which is gone, soft-deleted, or (for a substitute) set to nothing.</summary>
    /// <remarks>Почему так — `docs/decisions/combos.md`</remarks>

    private static async Task ValidateReferencesAsync(AppDbContext db, Combo combo, CancellationToken cancellationToken)
    {
        var productIds = combo.Components
            .Select(component => component.ProductId)
            .Concat(combo.Components.Where(component => component.SubstituteProductId.HasValue)
                .Select(component => component.SubstituteProductId!.Value))
            .Distinct()
            .ToList();

        // IsDeleted in the query, not after: a soft-deleted row is still findable by id, and "the
        // product exists" would then be true for a dish that cannot be sold or printed.
        var sellable = await db.Products.AsNoTracking()
            .Where(product => productIds.Contains(product.Id) && !product.IsDeleted)
            .Select(product => product.Id)
            .ToListAsync(cancellationToken);

        foreach (var component in combo.Components)
        {
            if (!sellable.Contains(component.ProductId))
                throw new ValidationFailureException("Компонент комбо ссылается на блюдо, которого нет в каталоге.");

            if (component.SubstituteProductId is { } substituteId && !sellable.Contains(substituteId))
                throw new ValidationFailureException("Замена компонента ссылается на блюдо, которого нет в каталоге.");
        }
    }

    /// <summary>FULL REPLACEMENT of the slot set — see `IComboService.SaveComboAsync` for why a partial write is not an option here.</summary>
    /// <remarks>Почему так — `docs/decisions/combos.md`</remarks>

    private static void SyncComponents(AppDbContext db, Combo existing, IReadOnlyCollection<ComboComponent> incoming)
    {
        var keptIds = new List<Guid>();

        foreach (var component in incoming)
        {
            var slotId = component.Id == Guid.Empty ? Guid.NewGuid() : component.Id;
            var current = existing.Components.FirstOrDefault(candidate => candidate.Id == slotId);

            if (current is null)
            {
                /// <summary>Through the DbSet, not the loaded collection: an entity pushed into the collection of a tracked parent is tracked as Modified (its key is already assi…</summary>
                /// <remarks>Почему так — `docs/decisions/combos.md`</remarks>

                db.ComboComponents.Add(new ComboComponent
                {
                    Id = slotId,
                    ComboId = existing.Id,
                    ProductId = component.ProductId,
                    QuantityPerUnit = component.QuantityPerUnit,
                    ComponentPriceKopecks = component.ComponentPriceKopecks,
                    SubstituteProductId = component.SubstituteProductId
                });
                keptIds.Add(slotId);
                continue;
            }

            current.ProductId = component.ProductId;
            current.QuantityPerUnit = component.QuantityPerUnit;
            current.ComponentPriceKopecks = component.ComponentPriceKopecks;
            current.SubstituteProductId = component.SubstituteProductId;
            keptIds.Add(current.Id);
        }

        var removed = existing.Components.Where(component => !keptIds.Contains(component.Id)).ToList();
        if (removed.Count > 0) db.ComboComponents.RemoveRange(removed);
    }

    public async Task DeleteComboAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var combo = await db.Combos.FirstOrDefaultAsync(current => current.Id == id, cancellationToken);
        if (combo is null) return;

        // Soft delete, like a product: sold bundles stay on their receipts, and their composition is a snapshot rather than a reference, so nothing about an old order changes when a bundle leaves the catalogue.

        combo.IsDeleted = true;
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Combo {ComboId} «{Name}» soft deleted", id, combo.Name);
    }

    // ─── Sale side ───

    /// <inheritdoc />
    public async Task<IReadOnlyList<SaleComposition>> ResolveSaleCompositionsAsync(
        IReadOnlyList<CheckoutLine> lines,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lines);

        var bundleIds = BundleIdsOf(lines);

        // Nothing on the cart is a bundle: no read, and every line comes back as an ordinary dish.
        if (bundleIds.Count == 0) return Ordinary(lines.Count);

        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var templates = await LoadTemplatesAsync(db, bundleIds, cancellationToken);

        var resolved = new List<SaleComposition>(lines.Count);
        foreach (var line in lines)
        {
            if (line.Components is not { Count: > 0 })
            {
                resolved.Add(new SaleComposition(null, []));
                continue;
            }

            var template = RequireTemplate(templates, line);
            resolved.Add(new SaleComposition(template.PriceKopecks, ResolveOne(template, line)));
        }

        return resolved;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<long?>> ResolveSalePricesAsync(
        IReadOnlyList<CheckoutLine> lines,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lines);

        var bundleIds = BundleIdsOf(lines);
        if (bundleIds.Count == 0) return Enumerable.Repeat((long?)null, lines.Count).ToList();

        // The same loader the strict path uses, projected to the two columns this answer needs: an order editor asks "what may this cost", not "what is in it",…
        // Почему так — `docs/decisions/combos.md`

        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var prices = await db.Combos.AsNoTracking()
            .Where(combo => bundleIds.Contains(combo.Id) && !combo.IsDeleted)
            .Select(combo => new { combo.Id, combo.PriceKopecks })
            .ToListAsync(cancellationToken);
        var byId = prices.ToDictionary(price => price.Id, price => price.PriceKopecks);

        return lines
            .Select(line => line.Components is { Count: > 0 } && byId.TryGetValue(line.ProductId, out var price)
                ? price
                : (long?)null)
            .ToList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<IReadOnlyList<CheckoutComponent>>> ResolveSaleComponentsAsync(
        IReadOnlyList<CheckoutLine> lines,
        CancellationToken cancellationToken = default)
    {
        var compositions = await ResolveSaleCompositionsAsync(lines, cancellationToken);
        return compositions.Select(composition => composition.Components).ToList();
    }

    /// <summary>The distinct bundles named on these lines — i.e.</summary>
    /// <remarks>Почему так — `docs/decisions/combos.md`</remarks>

    private static List<Guid> BundleIdsOf(IReadOnlyList<CheckoutLine> lines) =>
        lines
            .Where(line => line.Components is { Count: > 0 })
            .Select(line => line.ProductId)
            .Distinct()
            .ToList();

    /// <summary>Every bundle named on the cart in ONE read, with the slots and both products behind them. A per-line query would be a round trip per combo line, and the same bundle can be on the cart more than once.</summary>

    private static Task<List<Combo>> LoadTemplatesAsync(
        AppDbContext db,
        IReadOnlyCollection<Guid> bundleIds,
        CancellationToken cancellationToken) =>
        db.Combos
            .Include(combo => combo.Components)
                .ThenInclude(component => component.Product)
            .Include(combo => combo.Components)
                .ThenInclude(component => component.SubstituteProduct)
            .AsSplitQuery()
            .Where(combo => bundleIds.Contains(combo.Id) && !combo.IsDeleted)
            .ToListAsync(cancellationToken);

    private static List<SaleComposition> Ordinary(int count) =>
        Enumerable.Range(0, count).Select(_ => new SaleComposition(null, (IReadOnlyList<CheckoutComponent>)[])).ToList();

    /// <summary>The live template of a line, or a refusal that names the bundle. A soft-deleted one counts as gone: it left the menu, and selling it again would be selling something the catalogue no longer offers.</summary>

    private static Combo RequireTemplate(List<Combo> templates, CheckoutLine line) =>
        templates.FirstOrDefault(combo => combo.Id == line.ProductId)
        ?? throw new ValidationFailureException(
            $"Комбо «{line.ProductName}» больше нет в каталоге. Обновите меню и соберите заказ заново.");

    /// <summary>One line's composition, rebuilt from the catalogue.</summary>
    /// <remarks>Почему так — `docs/decisions/combos.md`</remarks>

    private IReadOnlyList<CheckoutComponent> ResolveOne(Combo template, CheckoutLine line)
    {
        var components = new List<CheckoutComponent>(line.Components!.Count);

        foreach (var requested in line.Components!)
        {
            var slot = template.Components.FirstOrDefault(candidate => candidate.ProductId == requested.ProductId)
                ?? throw new ValidationFailureException(
                    $"Состав комбо «{template.Name}» изменился: блюдо «{requested.ProductName}» в него больше не входит. Обновите меню.");

            components.Add(ResolveSlot(slot));
        }

        return components;
    }

    /// <summary>One slot: which dish is actually sold for it, and what it counts for.</summary>
    /// <remarks>Почему так — `docs/decisions/combos.md`</remarks>

    private CheckoutComponent ResolveSlot(ComboComponent slot)
    {
        if (IsSellable(slot.Product))
        {
            var product = slot.Product!;
            return Describe(slot, product,
                unitKopecks: ComboPricing.ResolveUnitKopecks(slot.ComponentPriceKopecks, product.PriceKopecks));
        }

        if (slot.SubstituteProduct is { } substitute && IsSellable(substitute))
        {
            // Logged, because a substitution is a decision the operator never made and the manager
            // reading the shift report should be able to see that it happened.
            logger.LogInformation(
                "Combo slot: {SoldOut} is unavailable, substituting {Substitute} for combo {ComboId}",
                SoldOutName(slot), substitute.Name, slot.ComboId);

            return Describe(slot, substitute,
                unitKopecks: ComboPricing.ResolveUnitKopecks(slot.ComponentPriceKopecks, substitute.PriceKopecks));
        }

        /// <summary>No substitute, or the substitute is out too.</summary>
        /// <remarks>Почему так — `docs/decisions/combos.md`</remarks>

        var substituteName = slot.SubstituteProduct?.Name;
        var detail = substituteName is null
            ? "замена не настроена"
            : $"замена «{substituteName}» тоже недоступна";

        throw new ValidationFailureException(
            $"Комбо нельзя продать: «{SoldOutName(slot)}» нет в наличии, {detail}. Замените состав комбо или выберите другое блюдо.");
    }

    /// <summary>The dish that is out, named.</summary>
    /// <remarks>Почему так — `docs/decisions/combos.md`</remarks>

    private static string SoldOutName(ComboComponent slot) =>
        slot.Product?.Name ?? slot.ProductId.ToString();

    /// <summary>The snapshot row for a slot: the dish that is ACTUALLY being sold under it, at the price that slot is charged, with the same dish's à la carte price a…</summary>
    /// <remarks>Почему так — `docs/decisions/combos.md`</remarks>

    private static CheckoutComponent Describe(ComboComponent slot, Product product, long unitKopecks) => new(
        product.Id,
        product.Name,
        slot.QuantityPerUnit,
        unitKopecks,
        product.PriceKopecks);

    /// <summary>"Sellable" means available AND not soft-deleted.</summary>
    /// <remarks>Почему так — `docs/decisions/combos.md`</remarks>

    private static bool IsSellable(Product? product) => product is { IsAvailable: true, IsDeleted: false };
}
