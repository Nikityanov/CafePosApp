using CafePos.Core.Data;

namespace CafePos.Core.Schema.Migrations;

/// <summary>
/// Version 13 — <c>Orders.SeenAt TEXT NULL</c>: when the operator last looked at an order that had
/// already become ready.
/// </summary>
/// <remarks>
/// <para>
/// <b>WHY A COLUMN, WHEN <c>Orders.ReadyAt</c> ALREADY EXISTS.</b> The question the dot answers is
/// "has anyone seen this since it became ready", and that needs BOTH moments. <c>ReadyAt</c> is already
/// recorded when the status moves to <see cref="Models.OrderStatus.Ready"/>; without a second
/// timestamp there is nothing to compare it against and the only honest answer would be to show the dot
/// to everyone forever. Comparing two stored instants is what makes it decidable.
/// </para>
/// <para>
/// <b>WHY STORED RATHER THAN HELD IN MEMORY.</b> A dot that forgets itself on restart is worse than no
/// dot: the operator learns to ignore it, and then a genuinely unseen ready order looks identical to a
/// seen one. The board reloads itself on an auto-refresh tick (see <c>AutoRefreshSeconds</c>), so an
/// in-memory flag would also be lost on every rebuild of the row list, not merely on app restart.
/// </para>
/// <para>
/// <b>NULL IS THE NORMAL FIRST STATE AND MEANS "NEVER LOOKED", NOT "UNKNOWN".</b> An order that has
/// just been created and is not ready carries no dot either way, so there is no need to write a
/// sentinel row per order at creation. A NULL on a ready order means the operator has not opened it
/// since it became ready — which is exactly the state that should draw attention.
/// </para>
/// <para>
/// <b>NO BACKFILL, AND THAT IS CORRECT.</b> On upgrade every existing order is treated as unseen. That
/// is the right default: the alternative — backfilling <c>SeenAt = CreatedAt</c> to suppress the
/// dots — would invent a claim that somebody looked at orders nobody has looked at since the upgrade,
/// and would silence the first genuinely-useful signal the board offers.
/// </para>
/// <para>
/// <b>NO INDEX, FOR THE REASON Migration012 STATES.</b> The EF model declares no index on
/// <c>Orders</c> for this column, and the parity test reads columns through <c>PRAGMA table_info</c>
/// rather than creating what it finds missing. An index invented here would exist on upgraded
/// terminals only and nowhere in the model, which is worse than none.
/// </para>
/// <para>
/// The name has no <c>Utc</c> suffix, deliberately: every other timestamp on <c>Orders</c> is
/// <c>CreatedAt</c> / <c>ReadyAt</c> / <c>RequestedAt</c> and stores UTC without saying so, and a
/// column named <c>SeenAtUtc</c> would have needed an explicit <c>HasColumnName</c> to stop EF looking
/// for a <c>SeenAt</c> that the migration had not created.
/// </para>
/// <para>
/// Added as a nullable TEXT column with no DEFAULT, so SQLite takes the O(1) path that adds no table
/// rewrite and writes no value for existing rows.
/// </para>
/// </remarks>
internal sealed class Migration013_OrderSeenAt : ISchemaMigration
{
    public int Version => 13;
    public string Name => "An order records when it was last seen after becoming ready";

    public async Task ApplyAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        await MigrationSql.AddColumnIfMissingAsync(
            db, "Orders", "SeenAt", "TEXT NULL", cancellationToken);
    }
}
