using System.Security.Claims;
using CypherCommerce.Data;
using CypherCommerce.Models;
using CypherCommerce.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity;

namespace CypherCommerce.Controllers;

[Authorize(Roles = "StoreOwner")]
[Route("admin")]
public class AdminController(CommerceDbContext db, CommerceService commerce,
    IWebHostEnvironment environment) : Controller
{
    [AllowAnonymous, HttpGet("login")]
    public IActionResult Login() => User.IsInRole("StoreOwner")
        ? RedirectToAction(nameof(Index)) : View();

    [AllowAnonymous, HttpPost("login"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Login(string username, string password)
    {
        var account = await db.OwnerAccounts.FirstOrDefaultAsync(x => x.Username == username && x.IsActive);
        var store = account is null ? null : await db.StoreProfiles.FirstOrDefaultAsync(x => x.Id == account.StoreId && x.IsActive);
        if (account is null || store is null ||
            new PasswordHasher<OwnerAccount>().VerifyHashedPassword(account, account.PasswordHash, password)
                == PasswordVerificationResult.Failed)
        {
            ModelState.AddModelError("", "اسم المستخدم أو كلمة المرور غير صحيحة.");
            return View();
        }
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, account.Username), new Claim(ClaimTypes.Role, "StoreOwner"),
                new Claim("OwnerAccountId", account.Id.ToString()), new Claim("StoreId", store.Id.ToString())],
            CookieAuthenticationDefaults.AuthenticationScheme));
        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal);
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("logout"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return RedirectToAction(nameof(Login));
    }

    [HttpGet("")]
    public async Task<IActionResult> Index()
    {
        var todayUtc = DateTime.UtcNow.Date;
        var weekUtc = todayUtc.AddDays(-6);
        var monthUtc = new DateTime(todayUtc.Year, todayUtc.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var orders = await db.Orders.AsNoTracking().Include(x => x.Lines)
            .OrderByDescending(x => x.CreatedAtUtc).ToListAsync();
        var bookedOrders = orders.Where(x => x.Status is not (OrderStatus.Cancelled or OrderStatus.NeedsReview)).ToList();
        var lowStock = await db.Products.AsNoTracking()
            .Where(x => x.IsActive && x.StockQuantity <= x.LowStockThreshold)
            .OrderBy(x => x.StockQuantity).ToListAsync();
        var vm = new DashboardViewModel
        {
            Store = await commerce.GetStoreAsync(),
            NewOrders = orders.Count(x => x.Status == OrderStatus.New),
            NeedsReview = orders.Count(x => x.Status == OrderStatus.NeedsReview),
            LowStockCount = lowStock.Count,
            OutOfStockCount = lowStock.Count(x => x.StockQuantity == 0),
            CategoryCount = await db.Categories.CountAsync(x => x.IsActive),
            ProductCount = await db.Products.CountAsync(),
            OrdersToday = orders.Count(x => x.CreatedAtUtc >= todayUtc),
            RevenueToday = bookedOrders.Where(x => x.CreatedAtUtc >= todayUtc).Sum(x => x.Total),
            RevenueThisWeek = bookedOrders.Where(x => x.CreatedAtUtc >= weekUtc).Sum(x => x.Total),
            RevenueThisMonth = bookedOrders.Where(x => x.CreatedAtUtc >= monthUtc).Sum(x => x.Total),
            OrdersThisWeek = orders.Count(x => x.CreatedAtUtc >= weekUtc),
            OrdersThisMonth = orders.Count(x => x.CreatedAtUtc >= monthUtc),
            CompletedOrders = orders.Count(x => x.Status == OrderStatus.Completed),
            CancelledOrders = orders.Count(x => x.Status == OrderStatus.Cancelled),
            AverageOrderValue = bookedOrders.Count == 0 ? 0 : bookedOrders.Average(x => x.Total),
            DeliveryFeesThisMonth = bookedOrders.Where(x => x.CreatedAtUtc >= monthUtc).Sum(x => x.DeliveryFee),
            UnitsSoldThisMonth = bookedOrders.Where(x => x.CreatedAtUtc >= monthUtc).SelectMany(x => x.Lines).Sum(x => x.Quantity),
            ActiveProductCount = await db.Products.CountAsync(x => x.IsActive),
            DailyOrders = Enumerable.Range(0, 7).Select(offset => weekUtc.AddDays(offset))
                .Select(day => (day, orders.Count(x => x.CreatedAtUtc.Date == day),
                    bookedOrders.Where(x => x.CreatedAtUtc.Date == day).Sum(x => x.Total))).ToList(),
            StatusCounts = Enum.GetValues<OrderStatus>()
                .Select(status => (status, orders.Count(x => x.Status == status))).ToList(),
            RecentOrders = orders.Take(8).ToList(),
            LowStockProducts = lowStock.Take(6).ToList(),
            TopProducts = bookedOrders.SelectMany(x => x.Lines)
                .GroupBy(x => x.ProductName).Select(x => (x.Key, x.Sum(y => y.Quantity)))
                .OrderByDescending(x => x.Item2).Take(5).ToList(),
            ChannelOrders = orders.GroupBy(x => x.SourceChannel)
                .Select(x => (x.Key, x.Count())).OrderByDescending(x => x.Item2).ToList()
        };
        return View(vm);
    }

    [HttpGet("products")]
    public async Task<IActionResult> Products() => View(await db.Products.AsNoTracking()
        .Include(x => x.SubCategory).ThenInclude(x => x!.ParentCategory)
        .OrderBy(x => x.Name).ToListAsync());

    [HttpGet("products/new")]
    public async Task<IActionResult> NewProduct()
    {
        await LoadSubCategoriesAsync();
        return View("ProductForm", new ProductInput());
    }

    [HttpGet("products/{id:int}/edit")]
    public async Task<IActionResult> EditProduct(int id)
    {
        var product = await db.Products.FindAsync(id);
        if (product is null) return NotFound();
        await LoadSubCategoriesAsync();
        return View("ProductForm", new ProductInput
        {
            Id = product.Id, Name = product.Name, Sku = product.Sku,
            Description = product.Description, SubCategoryId = product.SubCategoryId ?? 0,
            Unit = product.Unit, Aliases = product.Aliases,
            ImageUrl = product.ImageUrl, Price = product.Price,
            StockQuantity = product.StockQuantity,
            LowStockThreshold = product.LowStockThreshold, IsActive = product.IsActive
        });
    }

    [HttpPost("products/save"), ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveProduct(ProductInput input)
    {
        if (input.Id != 0 && !await db.Products.AnyAsync(x => x.Id == input.Id)) return NotFound();
        var sub = await db.SubCategories.Include(x => x.ParentCategory)
            .FirstOrDefaultAsync(x => x.Id == input.SubCategoryId);
        if (sub is null) ModelState.AddModelError(nameof(input.SubCategoryId), "اختر تصنيفاً فرعياً موجوداً.");
        if (!ModelState.IsValid)
        {
            await LoadSubCategoriesAsync();
            return View("ProductForm", input);
        }
        if (await db.Products.AnyAsync(x => x.Sku == input.Sku && x.Id != input.Id))
        {
            ModelState.AddModelError(nameof(input.Sku), "رمز المنتج مستخدم مسبقاً.");
            await LoadSubCategoriesAsync();
            return View("ProductForm", input);
        }
        Product product;
        if (input.Id == 0)
        {
            product = new Product();
            db.Products.Add(product);
        }
        else
        {
            product = await db.Products.FindAsync(input.Id) ?? throw new InvalidOperationException("Product disappeared while saving.");
        }
        var oldStock = product.StockQuantity;
        product.Name = input.Name.Trim();
        product.Sku = input.Sku.Trim();
        product.Description = input.Description.Trim();
        product.SubCategoryId = sub!.Id;
        product.Category = sub.ParentCategory.Name;
        product.Unit = input.Unit.Trim();
        product.Aliases = input.Aliases.Trim();
        product.Price = input.Price;
        product.StockQuantity = input.StockQuantity;
        product.LowStockThreshold = input.LowStockThreshold;
        product.IsActive = input.IsActive;
        product.UpdatedAtUtc = DateTime.UtcNow;
        if (input.Image is not null)
        {
            try { product.ImageUrl = await SaveImageAsync(input.Image); }
            catch (InvalidOperationException ex)
            {
                ModelState.AddModelError(nameof(input.Image), ex.Message);
                await LoadSubCategoriesAsync();
                return View("ProductForm", input);
            }
        }
        else if (!string.IsNullOrWhiteSpace(input.ImageUrl)) product.ImageUrl = input.ImageUrl;
        await using var transaction = await db.Database.BeginTransactionAsync();
        await db.SaveChangesAsync();
        if (oldStock != product.StockQuantity)
        {
            db.StockMovements.Add(new StockMovement
            {
                ProductId = product.Id,
                QuantityChange = product.StockQuantity - oldStock,
                Reason = input.Id == 0 ? "مخزون أولي" : "تعديل يدوي من لوحة التحكم"
            });
            await db.SaveChangesAsync();
        }
        await transaction.CommitAsync();
        TempData["Success"] = "تم حفظ المنتج بنجاح.";
        return RedirectToAction(nameof(Products));
    }

    [HttpGet("inventory")]
    public async Task<IActionResult> Inventory() => View(await db.StockMovements.AsNoTracking()
        .Include(x => x.Product).OrderByDescending(x => x.CreatedAtUtc)
        .Take(150).ToListAsync());

    [HttpGet("orders")]
    public async Task<IActionResult> Orders(string? status)
    {
        var query = db.Orders.AsNoTracking().Include(x => x.Lines).AsQueryable();
        if (Enum.TryParse<OrderStatus>(status, true, out var parsed))
            query = query.Where(x => x.Status == parsed);
        ViewBag.Status = status;
        return View(await query.OrderByDescending(x => x.CreatedAtUtc).ToListAsync());
    }

    [HttpGet("orders/{id:int}")]
    public async Task<IActionResult> Order(int id)
    {
        var order = await db.Orders.AsNoTracking().Include(x => x.Lines)
            .FirstOrDefaultAsync(x => x.Id == id);
        if (order is null) return NotFound();
        ViewBag.Products = await db.Products.AsNoTracking().Where(x => x.IsActive)
            .OrderBy(x => x.Name).ToListAsync();
        return View(order);
    }

    [HttpPost("orders/{id:int}/confirm-chat"), ValidateAntiForgeryToken]
    public async Task<IActionResult> ConfirmChat(int id, [FromForm] Dictionary<int, int> matches)
    {
        try
        {
            await commerce.ConfirmAgentOrderAsync(id, matches);
            TempData["Success"] = "تم تأكيد الطلب وحجز المنتجات.";
        }
        catch (InvalidOperationException ex) { TempData["Error"] = ex.Message; }
        return RedirectToAction(nameof(Order), new { id });
    }

    [HttpPost("orders/{id:int}/status"), ValidateAntiForgeryToken]
    public async Task<IActionResult> SetStatus(int id, OrderStatus status)
    {
        try
        {
            await commerce.ChangeStatusAsync(id, status);
            TempData["Success"] = "تم تحديث حالة الطلب.";
        }
        catch (InvalidOperationException ex) { TempData["Error"] = ex.Message; }
        return RedirectToAction(nameof(Order), new { id });
    }

    [HttpGet("settings")]
    public async Task<IActionResult> Settings()
    {
        var store = await commerce.GetStoreAsync();
        if (store.WhatsAppEnabled || store.WebsiteAssistantEnabled)
            ViewBag.BotFacts = await db.BotStoreFacts.FirstOrDefaultAsync();
        return View(store);
    }

    [HttpGet("services")]
    public async Task<IActionResult> Services()
    {
        ViewBag.Store = await commerce.GetStoreAsync();
        return View(await db.ServiceChangeRequests.AsNoTracking()
            .OrderByDescending(x => x.CreatedAtUtc).Take(50).ToListAsync());
    }

    [HttpPost("services/request"), ValidateAntiForgeryToken]
    public async Task<IActionResult> RequestServiceChange(ServiceChangeInput input)
    {
        var store = await commerce.GetStoreAsync();
        if (!ModelState.IsValid || !BusinessServices.IsKnown(input.ServiceCode) ||
            input.RequestedAction is not ("Add" or "Remove"))
        {
            TempData["Error"] = "اختر خدمة وإجراءً صحيحين.";
            return RedirectToAction(nameof(Services));
        }
        var enable = input.RequestedAction == "Add";
        if (BusinessServices.IsEnabled(store, input.ServiceCode) == enable)
            TempData["Error"] = "الخدمة في الحالة المطلوبة بالفعل.";
        else if (enable && input.ServiceCode == BusinessServices.WebsiteAssistant && !store.StorefrontEnabled)
            TempData["Error"] = "اطلب تفعيل المتجر الإلكتروني أولاً قبل مساعد الموقع.";
        else if (await db.ServiceChangeRequests.AnyAsync(x => x.ServiceCode == input.ServiceCode && x.Status == "Pending"))
            TempData["Error"] = "يوجد طلب قيد المراجعة لهذه الخدمة.";
        else
        {
            db.ServiceChangeRequests.Add(new ServiceChangeRequest
            {
                StoreId = store.Id, ServiceCode = input.ServiceCode,
                RequestedAction = input.RequestedAction, OwnerNote = input.OwnerNote?.Trim() ?? ""
            });
            await db.SaveChangesAsync();
            TempData["Success"] = "وصل طلب تغيير الخدمة إلى فريق Cypher للمراجعة.";
        }
        return RedirectToAction(nameof(Services));
    }

    [HttpPost("settings"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Settings(StoreSettingsInput input)
    {
        var store = await commerce.GetStoreAsync();
        if (input.DeliveryEnabled == true && string.IsNullOrWhiteSpace(input.DeliveryAreas))
            ModelState.AddModelError(nameof(input.DeliveryAreas), "حدد مناطق التوصيل، أو * إذا التوصيل متاح لكل العناوين.");
        if (!ModelState.IsValid)
        {
            ViewBag.BotFacts = new BotStoreFacts { StoreId = store.Id, WorkingHours = input.WorkingHours,
                DeliveryAreas = input.DeliveryAreas, DeliveryPolicy = input.DeliveryPolicy,
                ReturnPolicy = input.ReturnPolicy, OrderPolicy = input.OrderPolicy, DeliveryEnabled = input.DeliveryEnabled };
            return View(store);
        }
        store.Name = input.Name.Trim();
        store.Phone = input.Phone?.Trim() ?? "";
        store.Address = input.Address?.Trim() ?? "";
        if (store.WhatsAppEnabled)
        {
            var phone = input.WhatsApp?.Trim() ?? "";
            if (store.WhatsApp != phone)
                await db.Database.ExecuteSqlInterpolatedAsync($"IF OBJECT_ID(N'dbo.BotWhatsAppConnections',N'U') IS NOT NULL UPDATE dbo.BotWhatsAppConnections SET Status=N'disconnected',VerifiedAtUtc=NULL WHERE StoreId={store.Id}");
            store.WhatsApp = phone;
        }
        if (store.StorefrontEnabled)
        {
            store.Tagline = input.Tagline?.Trim() ?? "";
            store.About = input.About?.Trim() ?? "";
        }
        if (store.StorefrontEnabled || store.WhatsAppEnabled) store.DeliveryFee = input.DeliveryFee;
        if (store.WhatsAppEnabled || store.WebsiteAssistantEnabled)
        {
            var facts = await db.BotStoreFacts.FirstOrDefaultAsync();
            if (facts is null) { facts = new BotStoreFacts { StoreId = store.Id }; db.BotStoreFacts.Add(facts); }
            facts.WorkingHours = Clean(input.WorkingHours); facts.DeliveryAreas = Clean(input.DeliveryAreas);
            facts.DeliveryPolicy = Clean(input.DeliveryPolicy); facts.ReturnPolicy = Clean(input.ReturnPolicy);
            facts.OrderPolicy = Clean(input.OrderPolicy); facts.DeliveryEnabled = input.DeliveryEnabled;
            facts.UpdatedAtUtc = DateTime.UtcNow;
        }
        await db.SaveChangesAsync();
        TempData["Success"] = "تم حفظ إعدادات المتجر.";
        return RedirectToAction(nameof(Settings));
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private async Task<string> SaveImageAsync(IFormFile image)
    {
        if (image.Length == 0 || image.Length > 3 * 1024 * 1024)
            throw new InvalidOperationException("الصورة يجب أن تكون أقل من 3 ميجابايت.");
        var extension = image.ContentType switch
        {
            "image/jpeg" => ".jpg",
            "image/png" => ".png",
            "image/webp" => ".webp",
            _ => throw new InvalidOperationException("الصيغ المسموحة: JPG، PNG، WebP.")
        };
        var directory = Path.Combine(environment.WebRootPath, "uploads");
        Directory.CreateDirectory(directory);
        var filename = Guid.NewGuid().ToString("N") + extension;
        await using var file = System.IO.File.Create(Path.Combine(directory, filename));
        await image.CopyToAsync(file);
        return "/uploads/" + filename;
    }

    private async Task LoadSubCategoriesAsync() => ViewBag.SubCategories = await db.SubCategories
        .AsNoTracking().Include(x => x.ParentCategory)
        .OrderBy(x => x.ParentCategory.SortOrder).ThenBy(x => x.SortOrder)
        .ThenBy(x => x.Name).ToListAsync();

}
