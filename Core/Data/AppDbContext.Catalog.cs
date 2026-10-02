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
            entity.HasMany(order => order.Payments)
                .WithOne(payment => payment.Order)
                .HasForeignKey(payment => payment.OrderId)
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

        modelBuilder.Entity<OrderPayment>(entity =>
        {
            entity.HasKey(payment => payment.Id);
            entity.Property(payment => payment.Method).HasConversion<string>().HasMaxLength(30);
            // IsRefund deliberately keeps the default bool mapping (INTEGER), no converter — the
            // same reasoning as DraftOrder.IsActiveCart. Every row that exists before the refund
            // feature is a collection, so the column's DEFAULT 0 is already the correct backfill
            // and a value converter would only add a second way for a zero to be spelled.
            entity.Property(payment => payment.Note).HasMaxLength(300);
            // Payments of one order are read in payment order; the index serves the details screen,
            // the migration's NOT IN (SELECT OrderId ...) reconciliation check and the refund
            // walk, which reads one order's non-refunded rows in PaidAt order (FIFO mirroring).
            entity.HasIndex(payment => new { payment.OrderId, payment.PaidAt });
        });

        modelBuilder.Entity<Shift>(entity =>
        {
            entity.HasKey(shift => shift.Id);
            entity.HasIndex(shift => shift.IsActive);
            // Declared length matches OrderPayment.Note (300). CloseShiftAsync REFUSES an over-long
            // discrepancy reason rather than truncating it, the opposite of PaymentRecorder's note
            // handling on purpose — this one is the only record of why a drawer did not balance.
            entity.Property(shift => shift.CashDiscrepancyReason).HasMaxLength(300);
            // CountedCashKopecks / ExpectedCashKopecks / ReconciledAt keep the default nullable long?
            // / DateTimeOffset? mapping. No value converter: NULL means "never counted", and a
            // converter or a 0 default would erase that distinction — a counted 0 is missing money,
            // while NULL is a shift nobody stood at the till for.
        });
    }
}
