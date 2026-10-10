using CypherCommerce.Data;
using CypherCommerce.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using System.Text.RegularExpressions;

namespace CypherCommerce.Services;

public static class DemoStoreSetup
{
    public static async Task CreateAsync(IServiceProvider services, IConfiguration configuration)
    {
        using var serviceScope = services.CreateScope();
        var db = serviceScope.ServiceProvider.GetRequiredService<CommerceDbContext>();
        var scope = serviceScope.ServiceProvider.GetRequiredService<BusinessScope>();
        const string slug = "shadi-test";
        var username = configuration["Demo:OwnerUsername"] ?? "shadi-test";
        var password = configuration["Demo:OwnerPassword"];
        var phone = (configuration["Demo:Phone"] ?? "").Trim().TrimStart('+');
        if (string.IsNullOrWhiteSpace(password) || password.Length < 8
            || !Regex.IsMatch(phone, "^[1-9][0-9]{7,14}$") || username.Length is < 3 or > 100)
            throw new InvalidOperationException("Configure Demo:Phone and Demo:OwnerPassword (8+ characters) in User Secrets first.");
        var existing = await db.StoreProfiles.AsNoTracking().SingleOrDefaultAsync(x => x.Slug == slug);
        if (existing is not null)
        {
            if (!await db.OwnerAccounts.AnyAsync(x => x.StoreId == existing.Id && x.Username == username)
                || existing.Name != "متجر شادي التجريبي")
                throw new InvalidOperationException("The demo slug already belongs to another business; no changes made.");
            Console.WriteLine($"Demo already exists. StoreId={existing.Id}; /s/{slug}; owner={username}. No password or products changed.");
            return;
        }
        if (await db.OwnerAccounts.AnyAsync(x => x.Username == username))
            throw new InvalidOperationException("Demo owner username is already in use; no changes made.");
        await using var transaction = await db.Database.BeginTransactionAsync();
        var store = new StoreProfile { Slug = slug, Name = "متجر شادي التجريبي", Phone = phone, WhatsApp = phone,
            Address = "عمّان — متجر اختبار", Tagline = "بيانات تجريبية للمسابقة", About = "متجر مخصص لاختبار مساعد Cypher.",
            InventoryEnabled = true, WhatsAppEnabled = true, StorefrontEnabled = true, WebsiteAssistantEnabled = true, DeliveryFee = 1m };
        db.StoreProfiles.Add(store);
        await db.SaveChangesAsync();
        scope.Set(store.Id);
        var owner = new OwnerAccount { StoreId = store.Id, Username = username };
        owner.PasswordHash = new PasswordHasher<OwnerAccount>().HashPassword(owner, password);
        db.OwnerAccounts.Add(owner);
        var category = new ProductCategory { Name = "الصوص والتتبيلات" };
        db.Categories.Add(category);
        await db.SaveChangesAsync();
        var subcategory = new ProductSubCategory { ParentCategoryId = category.Id, Name = "الصوص" };
        db.SubCategories.Add(subcategory);
        await db.SaveChangesAsync();
        var variants = new[] { "حار", "باربكيو", "ثوم", "كاتشب", "مايونيز", "خردل", "تشيلي", "جبنة", "ترياكي", "صويا", "رانش", "سويت تشيلي", "بيستو", "طماطم", "ليمون" };
        for (var index = 0; index < variants.Length; index++)
            db.Products.Add(new Product { Name = $"صوص {variants[index]}", Sku = $"DEMO-SAUCE-{index + 1:00}",
                Category = category.Name, SubCategoryId = subcategory.Id, Unit = "عبوة", Aliases = $"سوس،صوص،{variants[index]}",
                Description = "صنف تجريبي لاختبار السعر والتوفر، وليس عرضاً تجارياً.", Price = 1.25m + index * 0.25m,
                StockQuantity = index == 14 ? 0 : 20, ImageUrl = "/images/placeholder.svg" });
        db.BotStoreFacts.Add(new BotStoreFacts { StoreId = store.Id, WorkingHours = "يومياً من ٩ صباحاً حتى ٩ مساءً (دوام تجريبي)",
            DeliveryEnabled = true, DeliveryAreas = "عمّان,عمان,Amman", DeliveryPolicy = "رسوم التوصيل التجريبية ١ د.أ داخل عمّان.",
            ReturnPolicy = "تواصل مع صاحب المتجر لتأكيد سياسة الإرجاع.", OrderPolicy = "كل طلب واتساب يخضع لمراجعة صاحب المتجر." });
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        Console.WriteLine($"Demo created. StoreId={store.Id}; /s/{slug}; owner={username}; 15 demo products. WhatsApp still requires verified pairing.");
    }
}
