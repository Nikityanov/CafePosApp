using CafePos.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace CafePos.Core.Data;

public partial class AppDbContext
{
    private static void ConfigureInventory(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Ingredient>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(200);
            entity.Property(e => e.Unit).HasMaxLength(50);
            entity.Property(e => e.Supplier).HasMaxLength(200);
        });

        modelBuilder.Entity<RecipeItem>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasOne(e => e.Product)
                .WithMany(p => p.RecipeItems)
                .HasForeignKey(e => e.ProductId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.Ingredient)
                .WithMany(i => i.RecipeItems)
                .HasForeignKey(e => e.IngredientId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<StockMovement>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Reason).IsRequired().HasMaxLength(200);
            // INTEGER, not TEXT: the journal reader decides whether a row is a receipt by this
            // column, and a string comparison is what breaks silently.
            entity.Property(e => e.Kind).HasConversion<int>();
            entity.HasIndex(e => new { e.IngredientId, e.CreatedAt });
            entity.HasOne(e => e.Ingredient)
                .WithMany()
                .HasForeignKey(e => e.IngredientId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }

    private static void ConfigureAuditAndDrafts(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<PriceRule>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(120);
            entity.HasOne(e => e.Product)
                .WithMany()
                .HasForeignKey(e => e.ProductId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<PriceHistoryEntry>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Reason).HasMaxLength(300);
            entity.HasOne(e => e.Product)
                .WithMany()
                .HasForeignKey(e => e.ProductId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(e => new { e.ProductId, e.ChangedAt });
        });

        modelBuilder.Entity<OrderStatusHistory>(entity =>
        {
            entity.HasKey(history => history.Id);
            entity.Property(history => history.Status).HasConversion<string>().HasMaxLength(30);
            entity.Property(history => history.Comment).HasMaxLength(300);
            entity.HasIndex(history => new { history.OrderId, history.ChangedAt });
        });

        modelBuilder.Entity<DraftOrder>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(120);
            // TEXT like Orders.OrderType — a parked cart that says "takeaway" has to say so in the
            // database, because the phone it may keep is decided by exactly this value.
            entity.Property(e => e.OrderType).HasConversion<string>().HasMaxLength(30);
            entity.Property(e => e.CustomerPhone).HasMaxLength(24);
            entity.HasMany(e => e.Items)
                .WithOne(item => item.DraftOrder)
                .HasForeignKey(item => item.DraftOrderId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<DraftOrderItem>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.ProductName).IsRequired().HasMaxLength(160);
            entity.Property(e => e.SelectedModifierName).HasMaxLength(120);
            entity.Property(e => e.SelectedVariantName).HasMaxLength(120);
            entity.HasMany(item => item.Components)
                .WithOne(component => component.DraftOrderItem)
                .HasForeignKey(component => component.DraftOrderItemId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<DraftOrderItemComponent>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.ProductName).IsRequired().HasMaxLength(160);
            // The autosave deletes and re-inserts every parked line, so the read is always "the
            // components of one line" and the index is on the foreign key alone.
            entity.HasIndex(e => e.DraftOrderItemId);
        });

        modelBuilder.Entity<SchemaVersion>(entity =>
        {
            entity.HasKey(e => e.Version);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(200);
        });
    }
}
