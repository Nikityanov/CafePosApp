using CafePos.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace CafePos.Core.Data;

/// <summary>Creates a small, working demo catalogue the first time the app starts.</summary>
internal static class DemoDataSeeder
{
    public static async Task SeedIfEmptyAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        if (await db.Products.IgnoreQueryFilters().AnyAsync(cancellationToken).ConfigureAwait(false)) return;

        var drinks = new Category { Id = Guid.NewGuid(), Name = "Напитки" };
        var desserts = new Category { Id = Guid.NewGuid(), Name = "Десерты" };
        db.Categories.AddRange(drinks, desserts);

        var milk = new ModifierGroup { Id = Guid.NewGuid(), Name = "Молоко" };
        milk.Options.Add(new ModifierOption { Id = Guid.NewGuid(), Name = "Обычное", ModifierGroup = milk });
        milk.Options.Add(new ModifierOption { Id = Guid.NewGuid(), Name = "Овсяное", ModifierGroup = milk });
        db.ModifierGroups.Add(milk);

        db.Products.AddRange(
            new Product { Id = Guid.NewGuid(), Name = "Капучино", Price = 220, ModifierGroup = milk, Category = drinks },
            new Product { Id = Guid.NewGuid(), Name = "Американо", Price = 160, Category = drinks },
            new Product { Id = Guid.NewGuid(), Name = "Чай", Price = 140, Category = drinks },
            new Product { Id = Guid.NewGuid(), Name = "Чизкейк", Price = 280, Category = desserts });

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
