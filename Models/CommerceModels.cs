using System.ComponentModel.DataAnnotations;

namespace CypherCommerce.Models;

public class StoreProfile
{
    public int Id { get; set; }
    [MaxLength(80)] public string Slug { get; set; } = "";
    public bool IsActive { get; set; } = true;
    public bool InventoryEnabled { get; set; } = true;
    public bool WhatsAppEnabled { get; set; }
    public bool StorefrontEnabled { get; set; } = true;
    public bool WebsiteAssistantEnabled { get; set; }
    [Required, MaxLength(100)] public string Name { get; set; } = "رِفد";
    [MaxLength(180)] public string Tagline { get; set; } = "";
    [MaxLength(1000)] public string About { get; set; } = "";
    [MaxLength(100)] public string Phone { get; set; } = "";
    [MaxLength(100)] public string WhatsApp { get; set; } = "";
    [MaxLength(200)] public string Address { get; set; } = "";
    public decimal DeliveryFee { get; set; }
}

public class ProductCategory
{
    public int Id { get; set; }
    public int StoreId { get; set; }
    [Required, MaxLength(80)] public string Name { get; set; } = "";
    [MaxLength(300)] public string Description { get; set; } = "";
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;
    public List<ProductSubCategory> SubCategories { get; set; } = [];
}

public class ProductSubCategory
{
    public int Id { get; set; }
    public int StoreId { get; set; }
    public int ParentCategoryId { get; set; }
    public ProductCategory ParentCategory { get; set; } = null!;
    [Required, MaxLength(80)] public string Name { get; set; } = "";
    [MaxLength(300)] public string Description { get; set; } = "";
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;
    public List<Product> Products { get; set; } = [];
}

public class Product
{
    public int Id { get; set; }
    public int StoreId { get; set; }
    [Required, MaxLength(140)] public string Name { get; set; } = "";
    [Required, MaxLength(50)] public string Sku { get; set; } = "";
    [MaxLength(1000)] public string Description { get; set; } = "";
    [MaxLength(80)] public string Category { get; set; } = "عام";
    public int? SubCategoryId { get; set; }
    public ProductSubCategory? SubCategory { get; set; }
    [MaxLength(50)] public string Unit { get; set; } = "قطعة";
    [MaxLength(500)] public string Aliases { get; set; } = "";
    [MaxLength(500)] public string? ImageUrl { get; set; }
    [Range(0, 1000000)] public decimal Price { get; set; }
    [Range(0, 1000000)] public int StockQuantity { get; set; }
    [Range(0, 1000000)] public int LowStockThreshold { get; set; } = 5;
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}

public enum OrderStatus { NeedsReview, New, Confirmed, Preparing, Ready, Completed, Cancelled }

public class ShopOrder
{
    public int Id { get; set; }
    public int StoreId { get; set; }
    [Required, MaxLength(24)] public string OrderNumber { get; set; } = "";
    [Required, MaxLength(40)] public string SourceChannel { get; set; } = "website";
    [MaxLength(150)] public string CustomerName { get; set; } = "";
    [MaxLength(40)] public string Phone { get; set; } = "";
    [MaxLength(500)] public string DeliveryAddress { get; set; } = "";
    [MaxLength(1000)] public string CustomerNote { get; set; } = "";
    [MaxLength(1000)] public string OwnerNote { get; set; } = "";
    [MaxLength(150)] public string? ExternalOrderId { get; set; }
    [MaxLength(150)] public string? ExternalConversationId { get; set; }
    public OrderStatus Status { get; set; } = OrderStatus.New;
    public decimal Subtotal { get; set; }
    public decimal DeliveryFee { get; set; }
    public decimal Total { get; set; }
    public bool StockReserved { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? ConfirmedAtUtc { get; set; }
    public List<OrderLine> Lines { get; set; } = [];
}

public class OrderLine
{
    public int Id { get; set; }
    public int StoreId { get; set; }
    public int OrderId { get; set; }
    public ShopOrder Order { get; set; } = null!;
    public int? ProductId { get; set; }
    public Product? Product { get; set; }
    [MaxLength(200)] public string ProductName { get; set; } = "";
    [Range(1, 100000)] public int Quantity { get; set; } = 1;
    public decimal UnitPrice { get; set; }
    public decimal LineTotal { get; set; }
    [MaxLength(500)] public string RawCustomerText { get; set; } = "";
    [MaxLength(500)] public string AiNote { get; set; } = "";
    public bool IsUnresolved { get; set; }
}

public class StockMovement
{
    public int Id { get; set; }
    public int StoreId { get; set; }
    public int ProductId { get; set; }
    public Product Product { get; set; } = null!;
    public int QuantityChange { get; set; }
    [MaxLength(300)] public string Reason { get; set; } = "";
    public int? OrderId { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
