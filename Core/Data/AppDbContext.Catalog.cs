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
            // PriceKopecks keeps the default INTEGER mapping with no converter, like Product's: it is
            // money the catalogue already speaks in kopecks, and a conversion would only be a second way
            // to spell the same integer. It is NOT NULL, and SaveComboAsync refuses zero — a sellable
            // item at no price is a catalogue error, and a NULL here would have to become 0 at every
            // read, i.e. a second silent place where a missing price turns into a free meal.
            entity.HasMany(combo => combo.Components)
                .WithOne(component => component.Combo)
                .HasForeignKey(component => component.ComboId)
                .OnDelete(DeleteBehavior.Cascade);
            // No index on IsDeleted, unlike Product: a catalogue of bundles is a handful of rows
            // where the menu screen reads all of them anyway, and a second single-column index here
            // would be a column SQLite has to keep in step with one it never chooses.
            // And no index on PriceKopecks for the same reason stated there: nothing queries a bundle
            // BY its price, so an index on it would be a second column SQLite keeps in step with one
            // it never chooses. Migration012 therefore creates no index at all — the model declares
            // none for Combos, and the parity test reads columns, so nothing would ever notice.
        });

        modelBuilder.Entity<ComboComponent>(entity =>
        {
            entity.HasKey(component => component.Id);
            // Two references to Products, so both navigations are named: the slot's dish and its
            // substitute. ComponentPriceKopecks keeps the default nullable long? mapping — its three
            // states (null = the dish price, 0 = free, a number = this price) are exactly what makes
            // it usable, and a non-nullable default of 0 would collapse "free" into "the dish is
            // free", which is a different statement about the catalogue.
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
            // SubstituteProductId gets EF's conventional index as a foreign key, and Migration010
            // creates it by name: it is never queried on its own (a sale loads the bundle's slots),
            // but a fresh install would have it and an upgraded one would not.
        });
    }

    private static void ConfigureOrders(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Order>(entity =>
        {
            entity.HasKey(order => order.Id);
            entity.Property(order => order.Status).HasConversion<string>().HasMaxLength(30);
            entity.Property(order => order.CancellationReason).HasMaxLength(300);
            // TEXT like Status and OrderPayment.Method, so an order's fulfilment mode is readable
            // straight out of SQLite during an investigation. 24 characters is the E.164 cap plus
            // room for the '+', and the value is written by PhoneNumber.Normalize, never typed free.
            entity.Property(order => order.OrderType).HasConversion<string>().HasMaxLength(30);
            entity.Property(order => order.CustomerPhone).HasMaxLength(24);
            entity.HasIndex(order => new { order.ShiftId, order.OrderNumber }).IsUnique();
            entity.HasIndex(order => order.Status);
            entity.HasIndex(order => order.CreatedAt);
            entity.HasIndex(order => order.ShiftId);
            // The schedule section reads this column, and it is also what makes a later move to SQL
            // ordering possible without a migration. Useless on its own today: SQLite cannot ORDER BY
            // a DateTimeOffset, so the queue sorts in memory over a materialised projection.
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
            // ListPriceKopecks keeps the default INTEGER mapping with no converter, like
            // DraftOrder.IsActiveCart: it is money the product price already wrote, so a conversion
            // would only be a second way to spell the same integer.
            // OrderItemComponent.ProductId is deliberately NOT a foreign key. The snapshot has to
            // outlive the catalogue entry it names — a sale does not stop having happened because the
            // dish was later renamed or removed.
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

        modelBuilder.Entity<CashMovement>(entity =>
        {
            entity.HasKey(movement => movement.Id);
            // TEXT like OrderPayment.Method, so the ledger is readable straight out of SQLite during
            // an investigation rather than being a column of ordinals only this build understands.
            entity.Property(movement => movement.Kind).HasConversion<string>().HasMaxLength(30);
            // Matches OrderPayment.Note (300) and Shift.CashDiscrepancyReason (300). The reason is
            // REFUSED rather than truncated when over-long, so this bound is an integrity guarantee
            // and never a silent cut — see CashLedgerService.
            entity.Property(movement => movement.Reason).HasMaxLength(300);
            // Every read of this table is "this shift's movements", both for the balance and for the
            // list on screen, so the index is on ShiftId alone and not on ShiftId + CreatedAt: the
            // balance sums without an order and the list orders in memory, and a composite index
            // would only make the second read pay for a sort the first one does not use.
            entity.HasIndex(movement => movement.ShiftId);
            // ReversesMovementId is deliberately NOT a foreign key. A self-reference that SQLite
            // enforces would mean deleting a movement — which nothing in the app does, but a
            // hand-edited database could — silently cascade a second deletion or fail the write.
            // The link is checked in CashLedgerService, where the refusal can be explained.
        });
    }
}
