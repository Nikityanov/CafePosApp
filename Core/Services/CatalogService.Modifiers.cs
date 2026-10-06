using CafePos.Core.Errors;
using CafePos.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace CafePos.Core.Services;

/// <summary>
/// One half of the catalogue service: modifiers.
/// </summary>
/// <remarks>
/// The class is already partial and was already 416 lines with 24 methods over five aggregates.
/// Splitting it by aggregate is pure movement: no type changes, no namespace change, no change
/// to the primary constructor or to DI. Only this part declares the constructor and the
/// interface - the others repeat neither.
/// </remarks>
public sealed partial class CatalogService
{
    // ─── Modifiers ───

    public async Task SaveModifierGroupAsync(ModifierGroup group, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(group.Name)) throw new ValidationFailureException("Укажите название группы.");
        if (group.Options.Count == 0) throw new ValidationFailureException("Добавьте хотя бы один вариант модификатора.");

        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var existing = await db.ModifierGroups
            .Include(item => item.Options)
            .FirstOrDefaultAsync(item => item.Id == group.Id, cancellationToken);

        if (existing is null)
        {
            db.ModifierGroups.Add(group);
        }
        else
        {
            existing.Name = group.Name;
            var keptIds = new List<Guid>();
            foreach (var option in group.Options)
            {
                if (string.IsNullOrWhiteSpace(option.Name)) continue;

                var current = existing.Options.FirstOrDefault(item =>
                        (option.Id != Guid.Empty && item.Id == option.Id)
                        || string.Equals(item.Name, option.Name, StringComparison.OrdinalIgnoreCase));

                if (current is null)
                {
                    var newOption = new ModifierOption
                    {
                        Id = option.Id == Guid.Empty ? Guid.NewGuid() : option.Id,
                        Name = option.Name.Trim(),
                        ModifierGroupId = existing.Id,
                        IsAvailable = option.IsAvailable
                    };
                    existing.Options.Add(newOption);
                    keptIds.Add(newOption.Id);
                }
                else
                {
                    current.Name = option.Name.Trim();
                    keptIds.Add(current.Id);
                }
            }

            var removed = existing.Options.Where(item => !keptIds.Contains(item.Id)).ToList();
            if (removed.Count > 0) db.ModifierOptions.RemoveRange(removed);
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteModifierGroupAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var group = await db.ModifierGroups.FirstOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (group is null) return;
        db.ModifierGroups.Remove(group);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task ToggleModifierOptionAvailabilityAsync(Guid optionId, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var option = await db.ModifierOptions.FirstOrDefaultAsync(item => item.Id == optionId, cancellationToken);
        if (option is null) return;
        option.IsAvailable = !option.IsAvailable;
        await db.SaveChangesAsync(cancellationToken);
    }


}
