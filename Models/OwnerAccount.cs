using System.ComponentModel.DataAnnotations;

namespace CypherCommerce.Models;

public sealed class OwnerAccount
{
    public int Id { get; set; }
    public int StoreId { get; set; }
    [Required, MaxLength(100)] public string Username { get; set; } = "";
    [Required, MaxLength(500)] public string PasswordHash { get; set; } = "";
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
