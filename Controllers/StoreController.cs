using System.Text.Json;
using CypherCommerce.Data;
using CypherCommerce.Models;
using CypherCommerce.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.RateLimiting;

namespace CypherCommerce.Controllers;

public class StoreController(CommerceDbContext db, CommerceService commerce, BusinessScope scope) : Controller
{
    private string CartKey => $"store-cart:{scope.StoreId}";
    private string LatestOrderKey => $"latest-order:{scope.StoreId}";
    private string StorePath(string path) => (HttpContext.Items["StoreBasePath"] as string ?? "") + path;

    [HttpGet("/shop")]
    [HttpGet("/products")]
    [HttpGet("/s/{slug}")]
    [HttpGet("/s/{slug}/shop")]
    public async Task<IActionResult> Index(string? q, string? category, int? subcategory)
    {
        var query = db.Products.AsNoTracking().Where(x => x.IsActive && x.SubCategory != null
            && x.SubCategory.IsActive && x.SubCategory.ParentCategory.IsActive);
        if (!string.IsNullOrWhiteSpace(q))
        {
            var search = q.Trim();
            query = query.Where(x => x.Name.Contains(search) || x.Description.Contains(search)
                                     || x.Aliases.Contains(search) || x.Sku.Contains(search));
        }
        if (!string.IsNullOrWhiteSpace(category))
            query = query.Where(x => x.SubCategory!.ParentCategory.Name == category);
        if (subcategory is > 0)
            query = query.Where(x => x.SubCategoryId == subcategory);
        return View(new StorefrontViewModel
        {
            Store = await commerce.GetStoreAsync(),
            Products = await query.OrderBy(x => x.Name).ToListAsync(),
            Categories = await db.Categories.AsNoTracking().Where(x => x.IsActive)
                .OrderBy(x => x.SortOrder).ThenBy(x => x.Name).Select(x => x.Name).ToListAsync(),
            SubCategories = await db.SubCategories.AsNoTracking().Include(x => x.ParentCategory)
                .Where(x => x.IsActive && x.ParentCategory.IsActive)
                .OrderBy(x => x.ParentCategory.SortOrder).ThenBy(x => x.SortOrder).ToListAsync(),
            Search = q,
            Category = category,
            SubCategoryId = subcategory,
            CartCount = ReadCart().Values.Sum()
        });
    }

    [HttpGet("/products/{id:int}")]
    [HttpGet("/s/{slug}/products/{id:int}")]
    public async Task<IActionResult> Product(int id)
    {
        var product = await db.Products.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id && x.IsActive
            && x.SubCategory != null && x.SubCategory.IsActive && x.SubCategory.ParentCategory.IsActive);
        if (product is null) return NotFound();
        ViewBag.Store = await commerce.GetStoreAsync();
        ViewBag.CartCount = ReadCart().Values.Sum();
        return View(product);
    }

    [HttpGet("/cart")]
    [HttpGet("/s/{slug}/cart")]
    public async Task<IActionResult> Cart() => View(await BuildCartAsync());

    [HttpPost("/cart/add"), ValidateAntiForgeryToken]
    [HttpPost("/s/{slug}/cart/add")]
    public async Task<IActionResult> AddToCart(int productId, int quantity = 1, string? returnTo = null)
    {
        var continuePath = ContinueShoppingPath(returnTo, productId);
        var product = await db.Products.AsNoTracking().FirstOrDefaultAsync(x => x.Id == productId && x.IsActive
            && x.SubCategory != null && x.SubCategory.IsActive && x.SubCategory.ParentCategory.IsActive);
        if (product is null) return NotFound();
        var cart = ReadCart();
        var desired = cart.GetValueOrDefault(productId) + Math.Max(1, quantity);
        if (desired > product.StockQuantity)
        {
            TempData["Error"] = "الكمية المطلوبة غير متاحة حالياً.";
            return LocalRedirect(continuePath);
        }
        cart[productId] = desired;
        SaveCart(cart);
        TempData["Success"] = "تمت إضافة المنتج إلى السلة. يمكنك متابعة التسوق أو فتح السلة من الأعلى.";
        return LocalRedirect(continuePath);
    }

    private string ContinueShoppingPath(string? returnTo, int productId)
    {
        var basePath = HttpContext.Items["StoreBasePath"] as string ?? "";
        if (!string.IsNullOrEmpty(returnTo) && Url.IsLocalUrl(returnTo) &&
            (returnTo == $"{basePath}/shop" || returnTo.StartsWith($"{basePath}/shop?", StringComparison.Ordinal) ||
             returnTo == $"{basePath}/products/{productId}"))
            return returnTo;
        return StorePath("/shop");
    }

    [HttpPost("/cart/update"), ValidateAntiForgeryToken]
    [HttpPost("/s/{slug}/cart/update")]
    public async Task<IActionResult> UpdateCart(int productId, int quantity)
    {
        var cart = ReadCart();
        if (quantity <= 0) cart.Remove(productId);
        else
        {
            var product = await db.Products.AsNoTracking().FirstOrDefaultAsync(x => x.Id == productId && x.IsActive
                && x.SubCategory != null && x.SubCategory.IsActive && x.SubCategory.ParentCategory.IsActive);
            if (product is null || quantity > product.StockQuantity)
                TempData["Error"] = "الكمية المطلوبة غير متاحة حالياً.";
            else cart[productId] = quantity;
        }
        SaveCart(cart);
        return LocalRedirect(StorePath("/cart"));
    }

    [HttpGet("/checkout")]
    [HttpGet("/s/{slug}/checkout")]
    public async Task<IActionResult> Checkout()
    {
        var cart = await BuildCartAsync();
        if (cart.Lines.Count == 0) return LocalRedirect(StorePath("/cart"));
        return View(new CheckoutViewModel { Cart = cart });
    }

    [HttpPost("/checkout"), ValidateAntiForgeryToken]
    [HttpPost("/s/{slug}/checkout")]
    public async Task<IActionResult> Checkout(CheckoutInput input)
    {
        var cart = await BuildCartAsync();
        if (cart.Lines.Count == 0) return LocalRedirect(StorePath("/cart"));
        if (!ModelState.IsValid)
            return View(new CheckoutViewModel { Cart = cart, Input = input });
        try
        {
            var order = await commerce.PlaceWebsiteOrderAsync(ReadCart(), input);
            SaveCart(new Dictionary<int, int>());
            HttpContext.Session.SetInt32(LatestOrderKey, order.Id);
            return LocalRedirect(StorePath($"/order/{order.Id}/confirmation"));
        }
        catch (InvalidOperationException ex)
        {
            ModelState.AddModelError("", ex.Message);
            return View(new CheckoutViewModel { Cart = await BuildCartAsync(), Input = input });
        }
    }

    [HttpGet("/order/{id:int}/confirmation")]
    [HttpGet("/s/{slug}/order/{id:int}/confirmation")]
    public async Task<IActionResult> Confirmation(int id)
    {
        if (HttpContext.Session.GetInt32(LatestOrderKey) != id) return NotFound();
        var order = await db.Orders.AsNoTracking().Include(x => x.Lines)
            .FirstOrDefaultAsync(x => x.Id == id && x.SourceChannel == "website");
        if (order is null) return NotFound();
        ViewBag.Store = await commerce.GetStoreAsync();
        return View(order);
    }

    [HttpGet("/track")]
    [HttpGet("/s/{slug}/track")]
    public IActionResult Track(string? number) => View(new OrderTrackingViewModel { OrderNumber = number?.Trim() ?? "" });

    [HttpPost("/track"), ValidateAntiForgeryToken, EnableRateLimiting("order-tracking")]
    [HttpPost("/s/{slug}/track")]
    public async Task<IActionResult> Track(OrderTrackingViewModel input)
    {
        input.OrderNumber = input.OrderNumber?.Trim().ToUpperInvariant() ?? "";
        if (!ModelState.IsValid) return View(input);
        var order = await db.Orders.AsNoTracking().FirstOrDefaultAsync(x =>
            x.OrderNumber == input.OrderNumber);
        if (order is null)
            ModelState.AddModelError("", "لم نجد طلباً مطابقاً. تحقق من رقم الطلب.");
        else input.Order = order;
        return View(input);
    }

    private Dictionary<int, int> ReadCart()
    {
        var raw = HttpContext.Session.GetString(CartKey);
        if (string.IsNullOrWhiteSpace(raw)) return new Dictionary<int, int>();
        try { return JsonSerializer.Deserialize<Dictionary<int, int>>(raw) ?? new(); }
        catch (JsonException) { return new(); }
    }

    private void SaveCart(Dictionary<int, int> cart) =>
        HttpContext.Session.SetString(CartKey, JsonSerializer.Serialize(cart));

    private async Task<CartViewModel> BuildCartAsync()
    {
        var cart = ReadCart();
        var products = await db.Products.AsNoTracking().Where(x => cart.Keys.Contains(x.Id) && x.IsActive
            && x.SubCategory != null && x.SubCategory.IsActive && x.SubCategory.ParentCategory.IsActive).ToListAsync();
        var store = await commerce.GetStoreAsync();
        return new CartViewModel
        {
            Lines = products.Select(x => new CartLineViewModel { Product = x, Quantity = cart[x.Id] }).ToList(),
            DeliveryFee = products.Count == 0 ? 0 : store.DeliveryFee
        };
    }
}
