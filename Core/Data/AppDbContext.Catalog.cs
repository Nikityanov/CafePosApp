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

        modelBuilder.Entity<Combo>(entity =>
        {
            entity.HasKey(combo => combo.Id);
            entity.Property(combo => combo.Name).IsRequired().HasMaxLength(160);
            /// <summary>PriceKopecks keeps the default INTEGER mapping with no converter, like Product's: it is money the catalogue already speaks in kopecks, and a conversio…</summary>
            /// <remarks>Почему так — `docs/decisions/schema.md`</remarks>

            entity.HasMany(combo => combo.Components)
                .WithOne(component => component.Combo)
                .HasForeignKey(component => component.ComboId)
                .OnDelete(DeleteBehavior.Cascade);
            // No index on IsDeleted, unlike Product: a catalogue of bundles is a handful of rows where the menu screen reads all of them anyway, and a second single…
            // Почему так — `docs/decisions/schema.md`

        });

        modelBuilder.Entity<ComboComponent>(entity =>
        {
            entity.HasKey(component => component.Id);
            /// <summary>Two references to Products, so both navigations are named: the slot's dish and its substitute.</summary>
            /// <remarks>Почему так — `docs/decisions/schema.md`</remarks>

            entity.HasOne(component => component.Product)
                .WithMany()
                .HasForeignKey(component => component.ProductId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(component => component.SubstituteProduct)
                .WithMany()
                .HasForeignKey(component => component.SubstituteProductId);
            // Every write-off of this slot starts from the dish, and an 86 report has to find the
            // slots that use a dish without loading the bundles themselves — hence both indexes.
            entity.HasIndex(component => component.ComboId);
            entity.HasIndex(component => component.ProductId);
            // SubstituteProductId gets EF's conventional index as a foreign key, and Migration010 creates it by name: it is never queried on its own (a sale loads t…
            // Почему так — `docs/decisions/schema.md`

        });
    }

    private static void ConfigureOrders(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Order>(entity =>
        {
            entity.HasKey(order => order.Id);
            entity.Property(order => order.Status).HasConversion<string>().HasMaxLength(30);
            entity.Property(order => order.CancellationReason).HasMaxLength(300);
            /// <summary>TEXT like Status and OrderPayment.Method, so an order's fulfilment mode is readable straight out of SQLite during an investigation.</summary>
            /// <remarks>Почему так — `docs/decisions/schema.md`</remarks>

            entity.Property(order => order.OrderType).HasConversion<string>().HasMaxLength(30);
            entity.Property(order => order.CustomerPhone).HasMaxLength(24);
            entity.HasIndex(order => new { order.ShiftId, order.OrderNumber }).IsUnique();
            entity.HasIndex(order => order.Status);
            entity.HasIndex(order => order.CreatedAt);
            entity.HasIndex(order => order.ShiftId);
            /// <summary>The schedule section reads this column, and it is also what makes a later move to SQL ordering possible without a migration.</summary>
            /// <remarks>Почему так — `docs/decisions/schema.md`</remarks>

            entity.HasIndex(order => order.RequestedAt);
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
            entity.HasMany(item => item.Components)
                .WithOne(component => component.OrderItem)
                .HasForeignKey(component => component.OrderItemId)
                .OnDelete(DeleteBehavior.Cascade);
            // ListPriceKopecks keeps the default INTEGER mapping with no converter, like DraftOrder.IsActiveCart: it is money the product price already wrote, so a…
            // Почему так — `docs/decisions/schema.md`

        });

        modelBuilder.Entity<OrderItemComponent>(entity =>
        {
            entity.HasKey(component => component.Id);
            entity.Property(component => component.ProductName).IsRequired().HasMaxLength(160);
            // Every read of this table is "one line's composition" — the receipt, the reprint and
            // the shift report's bundle section — so the index is on the foreign key alone.
            entity.HasIndex(component => component.OrderItemId);
        });

        modelBuilder.Entity<OrderPayment>(entity =>
        {
            entity.HasKey(payment => payment.Id);
            entity.Property(payment => payment.Method).HasConversion<string>().HasMaxLength(30);
            /// <summary>IsRefund deliberately keeps the default bool mapping (INTEGER), no converter — the same reasoning as DraftOrder.IsActiveCart.</summary>
            /// <remarks>Почему так — `docs/decisions/schema.md`</remarks>

            entity.Property(payment => payment.Note).HasMaxLength(300);
            /// <summary>Payments of one order are read in payment order; the index serves the details screen, the migration's NOT IN (SELECT OrderId ...) reconciliation check…</summary>
            /// <remarks>Почему так — `docs/decisions/schema.md`</remarks>

            entity.HasIndex(payment => new { payment.OrderId, payment.PaidAt });
        });

        modelBuilder.Entity<Shift>(entity =>
        {
            entity.HasKey(shift => shift.Id);
            entity.HasIndex(shift => shift.IsActive);
            /// <summary>Declared length matches OrderPayment.Note (300).</summary>
            /// <remarks>Почему так — `docs/decisions/schema.md`</remarks>

            entity.Property(shift => shift.CashDiscrepancyReason).HasMaxLength(300);
            // CountedCashKopecks / ExpectedCashKopecks / ReconciledAt keep the default nullable long?
            // Почему так — `docs/decisions/schema.md`

        });

        modelBuilder.Entity<CashMovement>(entity =>
        {
            entity.HasKey(movement => movement.Id);
            // TEXT like OrderPayment.Method, so the ledger is readable straight out of SQLite during
            // an investigation rather than being a column of ordinals only this build understands.
            entity.Property(movement => movement.Kind).HasConversion<string>().HasMaxLength(30);
            /// <summary>Matches OrderPayment.Note (300) and Shift.CashDiscrepancyReason (300). The reason is REFUSED rather than truncated when over-long, so this bound is an integrity guarantee and never a silent cut — see CashLedgerService.</summary>

            entity.Property(movement => movement.Reason).HasMaxLength(300);
            /// <summary>Every read of this table is "this shift's movements", both for the balance and for the list on screen, so the index is on ShiftId alone and not on Shi…</summary>
            /// <remarks>Почему так — `docs/decisions/schema.md`</remarks>

            entity.HasIndex(movement => movement.ShiftId);
            // ReversesMovementId is deliberately NOT a foreign key.
            // Почему так — `docs/decisions/schema.md`

        });
    }
}
