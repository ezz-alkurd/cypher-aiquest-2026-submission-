using System.ComponentModel.DataAnnotations;

namespace CypherCommerce.Models;

public sealed class PlatformOperator
{
    public int Id { get; set; }
    [Required, MaxLength(100)] public string Username { get; set; } = "";
    [Required] public byte[] SecretHash { get; set; } = [];
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
