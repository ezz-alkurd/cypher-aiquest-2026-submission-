using CypherCommerce.Models;
using CypherCommerce.Services;
using Microsoft.EntityFrameworkCore;

namespace CypherCommerce.Data;

public class CommerceDbContext(DbContextOptions<CommerceDbContext> options, BusinessScope scope) : DbContext(options)
{
    public int CurrentStoreId => scope.StoreId;
    public DbSet<StoreProfile> StoreProfiles => Set<StoreProfile>();
    public DbSet<Product> Products => Set<Product>();
    public DbSet<ProductCategory> Categories => Set<ProductCategory>();
    public DbSet<ProductSubCategory> SubCategories => Set<ProductSubCategory>();
    public DbSet<ShopOrder> Orders => Set<ShopOrder>();
    public DbSet<OrderLine> OrderLines => Set<OrderLine>();
    public DbSet<StockMovement> StockMovements => Set<StockMovement>();
    public DbSet<AccessRequest> AccessRequests => Set<AccessRequest>();
    public DbSet<OwnerAccount> OwnerAccounts => Set<OwnerAccount>();
    public DbSet<BotStoreFacts> BotStoreFacts => Set<BotStoreFacts>();
    public DbSet<PlatformOperator> PlatformOperators => Set<PlatformOperator>();
    public DbSet<ServiceChangeRequest> ServiceChangeRequests => Set<ServiceChangeRequest>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<StoreProfile>().HasIndex(x => x.Slug).IsUnique();
        modelBuilder.Entity<OwnerAccount>().HasIndex(x => x.Username).IsUnique();
        modelBuilder.Entity<PlatformOperator>().HasIndex(x => x.Username).IsUnique();
        modelBuilder.Entity<PlatformOperator>().Property(x => x.SecretHash).HasMaxLength(32);
        modelBuilder.Entity<ServiceChangeRequest>().HasOne(x => x.Store).WithMany()
            .HasForeignKey(x => x.StoreId).OnDelete(DeleteBehavior.NoAction);
        modelBuilder.Entity<ServiceChangeRequest>().HasIndex(x => new { x.StoreId, x.ServiceCode })
            .IsUnique().HasFilter("[Status] = 'Pending'");
        modelBuilder.Entity<ServiceChangeRequest>().HasIndex(x => new { x.Status, x.CreatedAtUtc });
        modelBuilder.Entity<ServiceChangeRequest>().HasQueryFilter(x => x.StoreId == CurrentStoreId);
        modelBuilder.Entity<Product>().HasIndex(x => new { x.StoreId, x.Sku }).IsUnique();
        modelBuilder.Entity<ProductCategory>().HasIndex(x => new { x.StoreId, x.Name }).IsUnique();
        modelBuilder.Entity<ProductSubCategory>().HasIndex(x => new { x.StoreId, x.ParentCategoryId, x.Name }).IsUnique();
        modelBuilder.Entity<Product>().HasQueryFilter(x => x.StoreId == CurrentStoreId);
        modelBuilder.Entity<BotStoreFacts>().HasQueryFilter(x => x.StoreId == CurrentStoreId);
        modelBuilder.Entity<ProductCategory>().HasQueryFilter(x => x.StoreId == CurrentStoreId);
        modelBuilder.Entity<ProductSubCategory>().HasQueryFilter(x => x.StoreId == CurrentStoreId);
        modelBuilder.Entity<ShopOrder>().HasQueryFilter(x => x.StoreId == CurrentStoreId);
        modelBuilder.Entity<OrderLine>().HasQueryFilter(x => x.StoreId == CurrentStoreId);
        modelBuilder.Entity<StockMovement>().HasQueryFilter(x => x.StoreId == CurrentStoreId);
        modelBuilder.Entity<ProductSubCategory>().HasOne(x => x.ParentCategory)
            .WithMany(x => x.SubCategories).HasForeignKey(x => x.ParentCategoryId)
            .OnDelete(DeleteBehavior.NoAction);
        modelBuilder.Entity<Product>().HasOne(x => x.SubCategory).WithMany(x => x.Products)
            .HasForeignKey(x => x.SubCategoryId).OnDelete(DeleteBehavior.NoAction);
        modelBuilder.Entity<ShopOrder>().HasIndex(x => x.OrderNumber).IsUnique();
        modelBuilder.Entity<ShopOrder>().HasIndex(x => new { x.StoreId, x.SourceChannel, x.ExternalOrderId })
            .IsUnique().HasFilter("[ExternalOrderId] IS NOT NULL");
        modelBuilder.Entity<Product>().Property(x => x.Price).HasPrecision(18, 2);
        modelBuilder.Entity<StoreProfile>().Property(x => x.DeliveryFee).HasPrecision(18, 2);
        modelBuilder.Entity<ShopOrder>().Property(x => x.Subtotal).HasPrecision(18, 2);
        modelBuilder.Entity<ShopOrder>().Property(x => x.DeliveryFee).HasPrecision(18, 2);
        modelBuilder.Entity<ShopOrder>().Property(x => x.Total).HasPrecision(18, 2);
        modelBuilder.Entity<OrderLine>().Property(x => x.UnitPrice).HasPrecision(18, 2);
        modelBuilder.Entity<OrderLine>().Property(x => x.LineTotal).HasPrecision(18, 2);
        modelBuilder.Entity<ShopOrder>().HasMany(x => x.Lines).WithOne(x => x.Order)
            .HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<OrderLine>().HasOne(x => x.Product).WithMany()
            .HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.SetNull);
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        GuardBusinessWrites();
        return base.SaveChangesAsync(cancellationToken);
    }

    public override int SaveChanges()
    {
        GuardBusinessWrites();
        return base.SaveChanges();
    }

    private void GuardBusinessWrites()
    {
        foreach (var entry in ChangeTracker.Entries())
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified or EntityState.Deleted)) continue;
            if (entry.Entity is not (Product or ProductCategory or ProductSubCategory or ShopOrder or OrderLine or StockMovement or Models.BotStoreFacts)) continue;
            if (CurrentStoreId <= 0) throw new InvalidOperationException("Business scope is required.");
            var property = entry.Property("StoreId");
            if (entry.State == EntityState.Added && (int)property.CurrentValue! == 0)
                property.CurrentValue = CurrentStoreId;
            if ((int)property.CurrentValue! != CurrentStoreId ||
                (entry.State != EntityState.Added && (int)property.OriginalValue! != CurrentStoreId))
                throw new InvalidOperationException("Cross-business write rejected.");
        }
    }
}
