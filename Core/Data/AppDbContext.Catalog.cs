using CafePos.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace CafePos.Core.Data;

public partial class AppDbContext
{
    private static void ConfigureCatalog(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ModifierGroup>(entity =>
        {
            entity.HasKey(group => group.Id);
            entity.Property(group => group.Name).IsRequired().HasMaxLength(120);
            entity.HasMany(group => group.Options)
                .WithOne(option => option.ModifierGroup)
                .HasForeignKey(option => option.ModifierGroupId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ModifierOption>(entity =>
        {
            entity.HasKey(option => option.Id);
            entity.Property(option => option.Name).IsRequired().HasMaxLength(120);
        });

        modelBuilder.Entity<ProductVariant>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(120);
            entity.HasOne(e => e.Product)
                .WithMany(p => p.Variants)
                .HasForeignKey(e => e.ProductId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Category>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(120);
        });

        modelBuilder.Entity<Product>(entity =>
        {
            entity.HasKey(product => product.Id);
            entity.Property(product => product.Name).IsRequired().HasMaxLength(160);
            entity.Property(product => product.PhotoPath).HasMaxLength(500);
            entity.Property(product => product.Allergens).HasMaxLength(300);
            entity.Property(product => product.Tags).HasMaxLength(300);
            entity.HasIndex(product => product.IsDeleted);
            entity.HasIndex(product => product.CategoryId);
            entity.HasOne(product => product.Category)
                .WithMany(category => category.Products)
                .HasForeignKey(product => product.CategoryId)
                .OnDelete(DeleteBehavior.SetNull);
            entity.HasOne(product => product.ModifierGroup)
                .WithMany(group => group.Products)
                .HasForeignKey(product => product.ModifierGroupId)
                .OnDelete(DeleteBehavior.SetNull);
        });
    }

    private static void ConfigureOrders(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Order>(entity =>
        {
            entity.HasKey(order => order.Id);
            entity.Property(order => order.Status).HasConversion<string>().HasMaxLength(30);
            entity.Property(order => order.CancellationReason).HasMaxLength(300);
            entity.HasIndex(order => new { order.ShiftId, order.OrderNumber }).IsUnique();
            entity.HasIndex(order => order.Status);
            entity.HasIndex(order => order.CreatedAt);
            entity.HasIndex(order => order.ShiftId);
            entity.HasMany(order => order.Items)
                .WithOne(item => item.Order)
                .HasForeignKey(item => item.OrderId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasMany(order => order.StatusHistory)
                .WithOne(history => history.Order)
                .HasForeignKey(history => history.OrderId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(order => order.Shift)
                .WithMany(shift => shift.Orders)
                .HasForeignKey(order => order.ShiftId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<OrderItem>(entity =>
        {
            entity.HasKey(item => item.Id);
            entity.Property(item => item.ProductName).IsRequired().HasMaxLength(160);
            entity.Property(item => item.SelectedModifierName).HasMaxLength(120);
            entity.Property(item => item.SelectedVariantName).HasMaxLength(120);
            entity.HasIndex(item => item.OrderId);
        });

        modelBuilder.Entity<Shift>(entity =>
        {
            entity.HasKey(shift => shift.Id);
            entity.HasIndex(shift => shift.IsActive);
        });
    }
}
