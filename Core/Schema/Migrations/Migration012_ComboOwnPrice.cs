using CafePos.Core.Data;

namespace CafePos.Core.Schema.Migrations;

/// <summary>
/// Version 12 — a bundle states its own price: <c>Combos.PriceKopecks INTEGER NOT NULL</c>.
/// </summary>
/// <para>
/// <b>WHY A BUNDLE HAS A PRICE OF ITS OWN AT ALL.</b> Until now it had none: the price was the sum of
/// its slots, so raising the price of a dish silently repriced every bundle containing it and nobody
/// decided anything. The reversal is deliberate (docs/PLAN-combos-order-details.md §1.3) — in Oracle
/// Simphony the price set in the item definition, not following the parts, is the documented
/// alternative to "Add Side Prices To Meal Price" — and the reason is ownership: accounting decides
/// the price and the discount, the till records it. A program that computes the price of a meal has
/// taken that decision away.
/// </para>
/// <para>
/// <b>THE BACKFILL IS THE COMPONENT SUM, AND THAT IS A FACT, NOT A GUESS.</b> Every existing bundle
/// was in fact charging exactly that: the old model made the sum the price, so the sum IS the price
/// these bundles have been selling at. Writing anything else would either reprice the catalogue on the
/// day of the upgrade (0 — every bundle suddenly free, which is money nobody authorised) or invent a
/// number from nothing. The sum can legitimately be 0 for a bundle whose slots are all free; that is
/// not corrected here on purpose, because the next save of that bundle is where a human decides its
/// price, and until then the record matches what it charged.
/// <para>
/// <c>COALESCE(cc.ComponentPriceKopecks, p.PriceKopecks)</c> is the same three-state rule the code
/// applies in <c>ComboPricing.ResolveUnitKopecks</c>: a NULL slot price means "the dish's own price",
/// and 0 means free. <c>× cc.QuantityPerUnit</c> is there because the multiplicity lives in the slot
/// and not in the line quantity. The LEFT JOIN, unlike Migration010's, is deliberate: a slot whose dish
/// row is missing cannot be priced, and an INNER JOIN would quietly drop it from the sum and
/// understate the bundle rather than admitting the gap — the FK makes that impossible from the app, but
/// a hand-edited database can still produce it and a smaller number would be the worse answer.
/// </para>
/// <para>
/// <b>NO INDEX IS CREATED HERE, AND THAT IS CORRECT RATHER THAN AN OMISSION.</b> The EF model declares
/// no index on <c>Combos</c> at all (see <c>AppDbContext.ConfigureCatalog</c>), and the parity test
/// reads columns through <c>PRAGMA table_info</c> and never creates a missing index, so an index
/// invented here would exist on upgraded terminals only and nowhere in the model. The rule that matters
/// — create every index the model declares, explicitly — has nothing to do for this version.
/// </para>
/// <para>
/// The column is added with a literal DEFAULT 0, which is O(1) on SQLite (no table rewrite), and the
/// UPDATE is the backfill. It is guarded by <c>WHERE PriceKopecks = 0</c> so a re-run after a partial
/// failure is harmless and cannot touch a bundle somebody has already re-priced: a bundle with a price
/// of its own is never zero, because <c>SaveComboAsync</c> refuses it.
/// </para>
/// </remarks>
internal sealed class Migration012_ComboOwnPrice : ISchemaMigration
{
    public int Version => 12;
    public string Name => "A bundle states its own price, backfilled from the sum of its slots";

    public async Task ApplyAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        await MigrationSql.AddColumnIfMissingAsync(
            db, "Combos", "PriceKopecks", "INTEGER NOT NULL DEFAULT 0", cancellationToken);

        // ALTER TABLE silently does nothing when Combos does not exist (MigrationSql guards it), so the
        // UPDATE is guarded the same way rather than throwing on a database that never got version 10.
        if (!await SqliteSchemaHelper.TableExistsAsync(db, "Combos", cancellationToken).ConfigureAwait(false))
            return;

        await SqliteSchemaHelper.ExecuteAsync(db,
            "UPDATE [Combos] SET [PriceKopecks] = COALESCE((" +
                "SELECT SUM(COALESCE([cc].[ComponentPriceKopecks], [p].[PriceKopecks]) * [cc].[QuantityPerUnit]) " +
                "FROM [ComboComponents] [cc] " +
                "LEFT JOIN [Products] [p] ON [p].[Id] = [cc].[ProductId] " +
                "WHERE [cc].[ComboId] = [Combos].[Id]), 0) " +
            "WHERE [PriceKopecks] = 0",
            cancellationToken);
    }
}
