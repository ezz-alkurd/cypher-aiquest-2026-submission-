using System.ComponentModel.DataAnnotations;

namespace CypherCommerce.Models;

public sealed class ServiceChangeRequest
{
    public int Id { get; set; }
    public int StoreId { get; set; }
    public StoreProfile Store { get; set; } = null!;
    [Required, MaxLength(32)] public string ServiceCode { get; set; } = "";
    [Required, MaxLength(8)] public string RequestedAction { get; set; } = "";
    [Required, MaxLength(12)] public string Status { get; set; } = "Pending";
    [MaxLength(500)] public string OwnerNote { get; set; } = "";
    [MaxLength(500)] public string OperatorNote { get; set; } = "";
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? ResolvedAtUtc { get; set; }
    public int? ResolvedByOperatorId { get; set; }
}

public sealed class ServiceSelectionInput
{
    public bool Inventory { get; set; }
    public bool WhatsApp { get; set; }
    public bool Storefront { get; set; }
    public bool WebsiteAssistant { get; set; }
}

public sealed class ServiceChangeInput
{
    [Required, MaxLength(32)] public string ServiceCode { get; set; } = "";
    [Required, MaxLength(8)] public string RequestedAction { get; set; } = "";
    [MaxLength(500)] public string? OwnerNote { get; set; }
}
