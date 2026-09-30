using CafePos.Core.Data;
using CafePos.Core.Errors;
using CafePos.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CafePos.Core.Services;

public sealed class DraftOrderService(
    IDbContextFactory<AppDbContext> factory,
    TimeProvider timeProvider,
    ILogger<DraftOrderService> logger) : IDraftOrderService
{
    private const string ActiveCartName = "Текущий чек";

    public async Task<CartSnapshot> LoadActiveCartAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var draft = await db.DraftOrders.AsNoTracking()
            .Include(order => order.Items)
            .FirstOrDefaultAsync(order => order.IsActiveCart, cancellationToken);

        return draft is null
            ? CartSnapshot.Empty
            : new CartSnapshot(draft.Items.Select(ToLine).ToList(), draft.UpdatedAt);
    }

    public async Task SaveActiveCartAsync(IReadOnlyList<CheckoutLine> lines, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var draft = await db.DraftOrders
            .Include(order => order.Items)
            .FirstOrDefaultAsync(order => order.IsActiveCart, cancellationToken);

        var now = timeProvider.GetUtcNow();
        if (lines.Count == 0)
        {
            if (draft is not null) db.DraftOrders.Remove(draft);
            await db.SaveChangesAsync(cancellationToken);
            return;
        }

        if (draft is null)
        {
            draft = new DraftOrder
            {
                Id = Guid.NewGuid(),
                Name = ActiveCartName,
                IsActiveCart = true,
                CreatedAt = now,
                ShiftId = await db.Shifts.Where(shift => shift.IsActive).Select(shift => (Guid?)shift.Id).FirstOrDefaultAsync(cancellationToken)
            };
            db.DraftOrders.Add(draft);
        }

        draft.UpdatedAt = now;

        // The old lines go away and a fresh set comes back. Both halves have to be stated
        // explicitly, and the reason is EF Core's change detection rather than this code:
        //
        // DraftOrderItem.Id is client-assigned (Guid.NewGuid in ToItem), so EF cannot tell a
        // brand-new item from an existing one by its key alone. When new items were only
        // *assigned* to draft.Items, change detection tracked them as Modified against the
        // Unchanged draft — never Added. The batch then ran
        //     DELETE FROM "DraftOrderItems"   (the old lines)
        //     UPDATE "DraftOrderItems" SET .. (the new lines, by the deleted keys)
        // and the UPDATE matched 0 rows, so every cart autosave after the first ended in
        // DbUpdateConcurrencyException and the draft was never written. The first save worked
        // only because the draft itself was Added, which cascades Added to its dependents.
        //
        // AddRange states the intent outright, so the batch is DELETE-then-INSERT.
        var replacement = lines.Select(line => ToItem(line, draft.Id)).ToList();
        db.DraftOrderItems.RemoveRange(draft.Items);
        draft.Items = replacement;
        db.DraftOrderItems.AddRange(replacement);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task ClearActiveCartAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var draft = await db.DraftOrders.FirstOrDefaultAsync(order => order.IsActiveCart, cancellationToken);
        if (draft is null) return;
        db.DraftOrders.Remove(draft);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<DraftOrder> ParkAsync(string? name, IReadOnlyList<CheckoutLine> lines, CancellationToken cancellationToken = default)
    {
        if (lines.Count == 0) throw new ValidationFailureException("Нечего откладывать: чек пуст.");

        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();
        var draft = new DraftOrder
        {
            Id = Guid.NewGuid(),
            Name = string.IsNullOrWhiteSpace(name) ? $"Чек {now.ToLocalTime():HH:mm}" : name.Trim(),
            CreatedAt = now,
            UpdatedAt = now,
            ShiftId = await db.Shifts.Where(shift => shift.IsActive).Select(shift => (Guid?)shift.Id).FirstOrDefaultAsync(cancellationToken)
        };
        draft.Items = lines.Select(line => ToItem(line, draft.Id)).ToList();
        db.DraftOrders.Add(draft);
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Cart parked as {Name} ({Lines} lines)", draft.Name, draft.Items.Count);
        return draft;
    }

    public async Task<List<DraftOrder>> GetParkedAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var drafts = await db.DraftOrders.AsNoTracking()
            .Include(order => order.Items)
            .Where(order => !order.IsActiveCart)
            .ToListAsync(cancellationToken);
        // SQLite cannot ORDER BY a DateTimeOffset column in SQL — sort in memory.
        return drafts.OrderByDescending(order => order.UpdatedAt).ToList();
    }

    public async Task<CartSnapshot> TakeAsync(Guid draftId, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var draft = await db.DraftOrders
            .Include(order => order.Items)
            .FirstOrDefaultAsync(order => order.Id == draftId, cancellationToken)
            ?? throw new EntityNotFoundException("Отложенный чек не найден.");

        var snapshot = new CartSnapshot(draft.Items.Select(ToLine).ToList(), draft.UpdatedAt);
        db.DraftOrders.Remove(draft);
        await db.SaveChangesAsync(cancellationToken);
        return snapshot;
    }

    public async Task DeleteAsync(Guid draftId, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var draft = await db.DraftOrders.FirstOrDefaultAsync(order => order.Id == draftId, cancellationToken);
        if (draft is null) return;
        db.DraftOrders.Remove(draft);
        await db.SaveChangesAsync(cancellationToken);
    }

    private static CheckoutLine ToLine(DraftOrderItem item) => new(
        item.ProductId, item.ProductName, item.Price, item.Quantity, item.SelectedModifierName, item.SelectedVariantName);

    private DraftOrderItem ToItem(CheckoutLine line, Guid draftId) => new()
    {
        Id = Guid.NewGuid(),
        DraftOrderId = draftId,
        ProductId = line.ProductId,
        ProductName = line.ProductName,
        Price = line.Price,
        Quantity = line.Quantity,
        SelectedModifierName = line.ModifierName,
        SelectedVariantName = line.VariantName
    };
}
