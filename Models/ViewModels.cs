using System.ComponentModel.DataAnnotations;

namespace CypherCommerce.Models;

public class StorefrontViewModel
{
    public StoreProfile Store { get; set; } = new();
    public List<Product> Products { get; set; } = [];
    public List<string> Categories { get; set; } = [];
    public List<ProductSubCategory> SubCategories { get; set; } = [];
    public string? Search { get; set; }
    public string? Category { get; set; }
    public int? SubCategoryId { get; set; }
    public int CartCount { get; set; }
}

public class CartLineViewModel
{
    public Product Product { get; set; } = null!;
    public int Quantity { get; set; }
    public decimal Total => Product.Price * Quantity;
}

public class CartViewModel
{
    public List<CartLineViewModel> Lines { get; set; } = [];
    public decimal Subtotal => Lines.Sum(x => x.Total);
    public decimal DeliveryFee { get; set; }
    public decimal Total => Subtotal + DeliveryFee;
}

public class CheckoutInput
{
    [Required(ErrorMessage = "الاسم مطلوب"), MaxLength(150)] public string CustomerName { get; set; } = "";
    [Required(ErrorMessage = "رقم الهاتف مطلوب"), MaxLength(40)] public string Phone { get; set; } = "";
    [Required(ErrorMessage = "العنوان مطلوب"), MaxLength(500)] public string DeliveryAddress { get; set; } = "";
    [MaxLength(1000)] public string? CustomerNote { get; set; }
}

public class CheckoutViewModel
{
    public CartViewModel Cart { get; set; } = new();
    public CheckoutInput Input { get; set; } = new();
}

public class OrderTrackingViewModel
{
    [Required(ErrorMessage = "رقم الطلب مطلوب"), MaxLength(24)] public string OrderNumber { get; set; } = "";
    [Required(ErrorMessage = "رقم الهاتف مطلوب"), MaxLength(40)] public string Phone { get; set; } = "";
    public ShopOrder? Order { get; set; }
}

public class DashboardViewModel
{
    public StoreProfile Store { get; set; } = new();
    public int NewOrders { get; set; }
    public int NeedsReview { get; set; }
    public int LowStockCount { get; set; }
    public int OutOfStockCount { get; set; }
    public int CategoryCount { get; set; }
    public int ProductCount { get; set; }
    public int OrdersToday { get; set; }
    public decimal RevenueToday { get; set; }
    public decimal RevenueThisWeek { get; set; }
    public decimal RevenueThisMonth { get; set; }
    public int OrdersThisWeek { get; set; }
    public int OrdersThisMonth { get; set; }
    public int CompletedOrders { get; set; }
    public int CancelledOrders { get; set; }
    public decimal AverageOrderValue { get; set; }
    public decimal DeliveryFeesThisMonth { get; set; }
    public int UnitsSoldThisMonth { get; set; }
    public int ActiveProductCount { get; set; }
    public List<(DateTime Day, int Orders, decimal Value)> DailyOrders { get; set; } = [];
    public List<(OrderStatus Status, int Count)> StatusCounts { get; set; } = [];
    public List<ShopOrder> RecentOrders { get; set; } = [];
    public List<Product> LowStockProducts { get; set; } = [];
    public List<(string Product, int Quantity)> TopProducts { get; set; } = [];
    public List<(string Channel, int Count)> ChannelOrders { get; set; } = [];
}

public class ProductInput
{
    public int Id { get; set; }
    [Required(ErrorMessage = "اسم المنتج مطلوب"), MaxLength(140)] public string Name { get; set; } = "";
    [Required(ErrorMessage = "رمز المنتج مطلوب"), MaxLength(50)] public string Sku { get; set; } = "";
    [MaxLength(1000)] public string Description { get; set; } = "";
    [Range(1, int.MaxValue, ErrorMessage = "اختر التصنيف الفرعي")] public int SubCategoryId { get; set; }
    [MaxLength(50)] public string Unit { get; set; } = "قطعة";
    [MaxLength(500)] public string Aliases { get; set; } = "";
    public string? ImageUrl { get; set; }
    [Range(0, 1000000)] public decimal Price { get; set; }
    [Range(0, 1000000)] public int StockQuantity { get; set; }
    [Range(0, 1000000)] public int LowStockThreshold { get; set; } = 5;
    public bool IsActive { get; set; } = true;
    public IFormFile? Image { get; set; }
}

public class CategoryInput
{
    public int Id { get; set; }
    [Required(ErrorMessage = "اسم التصنيف مطلوب"), MaxLength(80)] public string Name { get; set; } = "";
    [MaxLength(300)] public string Description { get; set; } = "";
    [Range(0, 10000)] public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;
}

public class SubCategoryInput
{
    public int Id { get; set; }
    [Range(1, int.MaxValue, ErrorMessage = "اختر التصنيف الرئيسي")] public int ParentCategoryId { get; set; }
    [Required(ErrorMessage = "اسم التصنيف الفرعي مطلوب"), MaxLength(80)] public string Name { get; set; } = "";
    [MaxLength(300)] public string Description { get; set; } = "";
    [Range(0, 10000)] public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;
}

public class AgentOrderRequest
{
    [Required, MaxLength(40)] public string Channel { get; set; } = "";
    [Required, MaxLength(150)] public string ExternalOrderId { get; set; } = "";
    [MaxLength(150)] public string? ExternalConversationId { get; set; }
    [MaxLength(150)] public string CustomerName { get; set; } = "";
    [MaxLength(40)] public string Phone { get; set; } = "";
    [MaxLength(500)] public string DeliveryAddress { get; set; } = "";
    [MaxLength(1000)] public string CustomerNote { get; set; } = "";
    public List<AgentOrderLineRequest> Lines { get; set; } = [];
}

public class AgentOrderLineRequest
{
    public int? ProductId { get; set; }
    [Range(1, 100000)] public int Quantity { get; set; } = 1;
    [MaxLength(500)] public string RawCustomerText { get; set; } = "";
    [MaxLength(500)] public string AiNote { get; set; } = "";
}

public class ProvisionBusinessInput
{
    [Required] public int RequestId { get; set; }
    [Required, MaxLength(100)] public string BusinessName { get; set; } = "";
    [Required, RegularExpression("^[a-z0-9]+(?:-[a-z0-9]+)*$"), MaxLength(80)]
    public string Slug { get; set; } = "";
    [Required, MaxLength(100)] public string Username { get; set; } = "";
    [Required, MinLength(12), MaxLength(200)] public string TemporaryPassword { get; set; } = "";
    public bool Inventory { get; set; }
    public bool WhatsApp { get; set; }
    public bool Storefront { get; set; }
    public bool WebsiteAssistant { get; set; }
}

public class StoreSettingsInput
{
    [MaxLength(1000)] public string? WorkingHours { get; set; }
    [MaxLength(1000)] public string? DeliveryAreas { get; set; }
    [MaxLength(1000)] public string? DeliveryPolicy { get; set; }
    [MaxLength(1000)] public string? ReturnPolicy { get; set; }
    [MaxLength(1000)] public string? OrderPolicy { get; set; }
    public bool? DeliveryEnabled { get; set; }
    [Required, MaxLength(100)] public string Name { get; set; } = "";
    [MaxLength(180)] public string? Tagline { get; set; }
    [MaxLength(1000)] public string? About { get; set; }
    [MaxLength(100)] public string? Phone { get; set; }
    [MaxLength(100)] public string? WhatsApp { get; set; }
    [MaxLength(200)] public string? Address { get; set; }
    [Range(0, 1000000)] public decimal DeliveryFee { get; set; }
}
