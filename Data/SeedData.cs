using CypherCommerce.Models;
using CypherCommerce.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace CypherCommerce.Data;

public static class SeedData
{
    public static async Task InitializeAsync(IServiceProvider services, bool seedDevelopmentPilot = true)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CommerceDbContext>();
        var businessScope = scope.ServiceProvider.GetRequiredService<BusinessScope>();
        var environment = scope.ServiceProvider.GetRequiredService<IWebHostEnvironment>();
        var config = scope.ServiceProvider.GetRequiredService<IConfiguration>();
        await db.Database.EnsureCreatedAsync();
        await SchemaUpgrade.ApplyAsync(db);

        if (seedDevelopmentPilot && environment.IsDevelopment() && !await db.StoreProfiles.AnyAsync())
        {
            db.StoreProfiles.Add(new StoreProfile { Slug = "pilot" });
            await db.SaveChangesAsync();
        }

        var pilot = await db.StoreProfiles.OrderBy(x => x.Id).FirstOrDefaultAsync();
        if (pilot is null) return;
        businessScope.Set(pilot.Id);
        // The old local owner credential is imported only for the development pilot.
        if (seedDevelopmentPilot && environment.IsDevelopment() &&
            !await db.OwnerAccounts.AnyAsync(x => x.StoreId == pilot.Id))
        {
            var password = config["Admin:Password"];
            if (!string.IsNullOrWhiteSpace(password))
            {
                var account = new OwnerAccount { StoreId = pilot.Id, Username = config["Admin:Username"] ?? "owner" };
                account.PasswordHash = new PasswordHasher<OwnerAccount>().HashPassword(account, password);
                db.OwnerAccounts.Add(account);
                await db.SaveChangesAsync();
            }
        }

        // Keep real products from older installations when upgrading categories.
        var uncategorized = await db.Products.Where(x => x.SubCategoryId == null).ToListAsync();
        if (uncategorized.Count == 0) return;

        foreach (var group in uncategorized.GroupBy(x => x.Category.Trim(), StringComparer.OrdinalIgnoreCase))
        {
            var categoryName = string.IsNullOrWhiteSpace(group.Key) ? "عام" : group.Key;
            var category = await db.Categories.FirstOrDefaultAsync(x => x.Name == categoryName);
            if (category is null)
            {
                category = new ProductCategory { Name = categoryName };
                db.Categories.Add(category);
                await db.SaveChangesAsync();
            }

            var subcategory = await db.SubCategories.FirstOrDefaultAsync(x =>
                x.ParentCategoryId == category.Id && x.Name == "أخرى");
            if (subcategory is null)
            {
                subcategory = new ProductSubCategory { ParentCategoryId = category.Id, Name = "أخرى" };
                db.SubCategories.Add(subcategory);
                await db.SaveChangesAsync();
            }

            foreach (var product in group)
            {
                product.Category = categoryName;
                product.SubCategoryId = subcategory.Id;
            }
        }
        await db.SaveChangesAsync();
    }
}
