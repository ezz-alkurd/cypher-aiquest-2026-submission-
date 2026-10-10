using System.ComponentModel.DataAnnotations;

namespace CypherCommerce.Models;

public sealed class BotStoreFacts
{
    [Key] public int StoreId { get; set; }
    [MaxLength(1000)] public string? WorkingHours { get; set; }
    [MaxLength(1000)] public string? DeliveryAreas { get; set; }
    [MaxLength(1000)] public string? DeliveryPolicy { get; set; }
    [MaxLength(1000)] public string? ReturnPolicy { get; set; }
    [MaxLength(1000)] public string? OrderPolicy { get; set; }
    public bool? DeliveryEnabled { get; set; }
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}
