using CafePos.Core.Common;
using CafePos.Core.Data;
using CafePos.Core.Errors;
using CafePos.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CafePos.Core.Services;

/// <summary>
/// Bundles in the catalogue, and the one place a requested composition is turned into one that may be
/// sold.
/// </summary>
/// <remarks>
/// <b>WHY SUBSTITUTION IS RESOLVED HERE AND NOT IN THE CHECKOUT.</b> Doing it where the catalogue is
/// loaded means the slots, their dishes and their substitutes are read once and every figure in the
/// sale comes from that one read, so the receipt, the stock write-off and the price control cannot
/// disagree about what was sold.
/// <para>
/// <b>AND WHAT SUBSTITUTION NO LONGER DOES.</b> It used to re-price the bundle: D365's rule is "if the
/// replacement is dearer, the kit price is recalculated", and when the price WAS the sum of the slots
/// that fell out of the arithmetic for free — nobody had written the rule, it was just what a sum
/// does. A bundle now has a price of its own (see <see cref="Combo"/>), so that rule has nothing left
/// to attach to, and that is the correct outcome rather than a missing feature: a café that sells a
/// 300 ₽ breakfast does not owe the customer 40 ₽ because the bread ran out, and if the difference is
/// worth recovering the answer is to change the price of the bundle on purpose, where accounting can
/// see it. The substitute is still written to the line's composition snapshot with its own price,
/// because the kitchen prints from that row and an audit six months later has to know what was
/// actually served.
/// </para>
/// </remarks>
public sealed class ComboService(
    IDbContextFactory<AppDbContext> factory,
    ILogger<ComboService> logger) : IComboService
{
    // ─── Reads ───

    public async Task<List<Combo>> GetCombosAsync(bool includeDeleted = false, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var combos = await LoadAsync(db, includeDeleted, cancellationToken);

        // Sorted in memory like every other ordered catalogue list in this codebase: SQLite cannot
        // ORDER BY a DateTimeOffset, and the ordering a catalogue screen shows has to be the ordering
        // one place defines (see CatalogService.GetCategoriesAsync for the same reason).
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

        // THE PRICE IS REQUIRED AND MUST BE POSITIVE. It is what the till charges, and a sellable item
        // at no price is a catalogue error rather than a bargain: a 0 here is a free meal that nobody
        // decided to give, and it is exactly the state a half-filled form produces. A bundle with no
        // slots is refused for the same reason — there is nothing to sell.
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

    /// <summary>
    /// Refuses a slot that points at a dish which is gone, soft-deleted, or (for a substitute) set to
    /// nothing. Checked against the DATABASE rather than against the caller, because the caller is a
    /// form that may have been open since before somebody deleted a dish — and a slot that survives
    /// pointing at a deleted product is exactly the state the sale-side resolver then has to refuse,
    /// with the operator looking at a bundle that looked fine when it was saved.
    /// </summary>
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

    /// <summary>
    /// FULL REPLACEMENT of the slot set — see <see cref="IComboService.SaveComboAsync"/> for why a
    /// partial write is not an option here. Slots are matched by identity so a slot that survives an
    /// edit keeps its identifier, and the ones nobody mentioned are deleted.
    /// </summary>
    private static void SyncComponents(AppDbContext db, Combo existing, IReadOnlyCollection<ComboComponent> incoming)
    {
        var keptIds = new List<Guid>();

        foreach (var component in incoming)
        {
            var slotId = component.Id == Guid.Empty ? Guid.NewGuid() : component.Id;
            var current = existing.Components.FirstOrDefault(candidate => candidate.Id == slotId);

            if (current is null)
            {
                // Through the DbSet, not the loaded collection: an entity pushed into the collection of
                // a tracked parent is tracked as Modified (its key is already assigned, so EF cannot
                // tell it is new) and EF then UPDATEs a row that does not exist. This trap is written
                // out at CatalogService.SyncVariants and PaymentRecorder.
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

        // Soft delete, like a product: sold bundles stay on their receipts, and their composition is a
        // snapshot rather than a reference, so nothing about an old order changes when a bundle leaves
        // the catalogue.
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

        // The same loader the strict path uses, projected to the two columns this answer needs: an
        // order editor asks "what may this cost", not "what is in it", and it must not drag every slot
        // and both products along to find out. One read of one row per bundle.
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

    /// <summary>
    /// The distinct bundles named on these lines — i.e. of the lines that HAVE slots. One list for both
    /// sale-side methods, because a rule written twice is a rule that will differ between the strict
    /// path and the lenient one the first time somebody adds a case to it.
    /// </summary>
    private static List<Guid> BundleIdsOf(IReadOnlyList<CheckoutLine> lines) =>
        lines
            .Where(line => line.Components is { Count: > 0 })
            .Select(line => line.ProductId)
            .Distinct()
            .ToList();

    /// <summary>
    /// Every bundle named on the cart in ONE read, with the slots and both products behind them. A
    /// per-line query would be a round trip per combo line, and the same bundle can be on the cart more
    /// than once.
    /// </summary>
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

    /// <summary>
    /// The live template of a line, or a refusal that names the bundle. A soft-deleted one counts as
    /// gone: it left the menu, and selling it again would be selling something the catalogue no longer
    /// offers.
    /// </summary>
    private static Combo RequireTemplate(List<Combo> templates, CheckoutLine line) =>
        templates.FirstOrDefault(combo => combo.Id == line.ProductId)
        ?? throw new ValidationFailureException(
            $"Комбо «{line.ProductName}» больше нет в каталоге. Обновите меню и соберите заказ заново.");

    /// <summary>
    /// One line's composition, rebuilt from the catalogue. Everything the client sent about the slots —
    /// names, quantities, prices — is dropped and replaced with what the catalogue says, so the sale,
    /// the receipt, the stock write-off and the price control all read the same rows.
    /// </summary>
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

    /// <summary>
    /// One slot: which dish is actually sold for it, and what it counts for.
    /// <para>
    /// <b>SUBSTITUTE RATHER THAN BLOCK, which is what every serious vendor does</b> — Simphony
    /// substitution groups, D365 product substitutes. Nobody takes a bundle off the menu because a side
    /// ran out; the customer is served the replacement.
    /// </para>
    /// <para>
    /// <b>AND THE PRICE DOES NOT MOVE WITH IT.</b> The old D365 behaviour — "if the replacement is
    /// dearer, the kit price is recalculated" — is deliberately gone. It existed because the price WAS
    /// the sum of the slots, so the difference arrived by arithmetic rather than by a rule anybody had
    /// to write; a bundle has a price of its own now, and the difference between a 200 ₽ espresso and a
    /// 240 ₽ decaf is the café's cost of doing business, not an amount to be added to a customer's bill.
    /// What the substitute is charged FOR here is the à la carte reference the bundle is measured
    /// against, and it is recorded on the line so the kitchen and any later audit can see that this is
    /// what was actually served.
    /// </para>
    /// <para>
    /// <b>THE SLOT'S OWN PRICE OUTRANKS THE DISH'S.</b> <see cref="ComboPricing.ResolveUnitKopecks"/>
    /// is applied to whatever dish ended up in the slot, so a slot priced explicitly (including a free
    /// one) keeps its figure across a substitution — the slot's price is a decision about the bundle, not
    /// about the dish that happened to be there. That is also plan 1.6's third case: a free slot's
    /// unavailability must not change the price, while still gating whether the sale may happen at all.
    /// </para>
    /// </summary>
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

        // No substitute, or the substitute is out too. Naming the dish is the whole message: a bundle
        // that will not sell is a catalogue or stock problem, and "комбо недоступно" leaves the
        // operator guessing which of four slots is the reason.
        var substituteName = slot.SubstituteProduct?.Name;
        var detail = substituteName is null
            ? "замена не настроена"
            : $"замена «{substituteName}» тоже недоступна";

        throw new ValidationFailureException(
            $"Комбо нельзя продать: «{SoldOutName(slot)}» нет в наличии, {detail}. Замените состав комбо или выберите другое блюдо.");
    }

    /// <summary>
    /// The dish that is out, named. Falls back to the identifier when the product row is gone — which
    /// a slot with a Restrict FK should make impossible from the app, and which a hand-edited database
    /// can still produce. An identifier in a refusal is read by nobody, but it is at least traceable to
    /// a catalogue row, where "блюдо из состава" would not be.
    /// </summary>
    private static string SoldOutName(ComboComponent slot) =>
        slot.Product?.Name ?? slot.ProductId.ToString();

    /// <summary>
    /// The snapshot row for a slot: the dish that is ACTUALLY being sold under it, at the price that
    /// slot is charged, with the same dish's à la carte price alongside it.
    /// <para>
    /// The name is the real dish's name, not the one the cart was holding: a substituted sale is printed
    /// from this row, so calling the replacement by the name of the dish that ran out would put a
    /// wrong name on a fiscal document. The same applies to the reference price — it is what THIS dish
    /// costs on its own, which is the figure the "was the bundle cheaper than its parts" signal needs.
    /// </para>
    /// </summary>
    private static CheckoutComponent Describe(ComboComponent slot, Product product, long unitKopecks) => new(
        product.Id,
        product.Name,
        slot.QuantityPerUnit,
        unitKopecks,
        product.PriceKopecks);

    /// <summary>
    /// "Sellable" means available AND not soft-deleted. Both, because a soft-deleted dish is out of the
    /// menu while its row — and therefore its price — is still there, and treating a deleted product as
    /// sellable would quietly keep selling it.
    /// </summary>
    private static bool IsSellable(Product? product) => product is { IsAvailable: true, IsDeleted: false };
}