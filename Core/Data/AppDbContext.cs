using CafePos.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace CafePos.Core.Data;

public partial class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<ModifierOption> ModifierOptions => Set<ModifierOption>();
    public DbSet<ModifierGroup> ModifierGroups => Set<ModifierGroup>();
    public DbSet<ProductVariant> ProductVariants => Set<ProductVariant>();
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<Product> Products => Set<Product>();
    public DbSet<Combo> Combos => Set<Combo>();
    public DbSet<ComboComponent> ComboComponents => Set<ComboComponent>();
    public DbSet<OrderItem> OrderItems => Set<OrderItem>();
    public DbSet<OrderItemComponent> OrderItemComponents => Set<OrderItemComponent>();
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<OrderPayment> OrderPayments => Set<OrderPayment>();
    public DbSet<Shift> Shifts => Set<Shift>();
    public DbSet<CashMovement> CashMovements => Set<CashMovement>();
    public DbSet<Ingredient> Ingredients => Set<Ingredient>();
    public DbSet<RecipeItem> RecipeItems => Set<RecipeItem>();
    public DbSet<PriceRule> PriceRules => Set<PriceRule>();
    public DbSet<PriceHistoryEntry> PriceHistoryEntries => Set<PriceHistoryEntry>();
    public DbSet<OrderStatusHistory> OrderStatusHistory => Set<OrderStatusHistory>();
    public DbSet<StockMovement> StockMovements => Set<StockMovement>();
    public DbSet<DraftOrder> DraftOrders => Set<DraftOrder>();
    public DbSet<DraftOrderItem> DraftOrderItems => Set<DraftOrderItem>();
    public DbSet<DraftOrderItemComponent> DraftOrderItemComponents => Set<DraftOrderItemComponent>();
    public DbSet<SchemaVersion> SchemaVersions => Set<SchemaVersion>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // NOTE: SQLite stores decimals as TEXT, hence:
        //  * every monetary value is persisted as INTEGER kopecks (see Common/Money.cs);
        //  * fractional quantities (stock, recipe amounts) keep decimal storage, but all
        //    comparisons/orderings on them happen in memory (see InventoryService).
        ConfigureCatalog(modelBuilder);
        ConfigureOrders(modelBuilder);
        ConfigureInventory(modelBuilder);
        ConfigureAuditAndDrafts(modelBuilder);
    }
}
