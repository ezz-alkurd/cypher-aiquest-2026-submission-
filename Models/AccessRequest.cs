using System.ComponentModel.DataAnnotations;

namespace CypherCommerce.Models;

public class AccessRequest
{
    public int Id { get; set; }
    [Required, MaxLength(120)] public string BusinessName { get; set; } = "";
    [Required, MaxLength(120)] public string ContactName { get; set; } = "";
    [Required, MaxLength(40)] public string Phone { get; set; } = "";
    [MaxLength(160)] public string Email { get; set; } = "";
    [Required, MaxLength(200)] public string RequestedServices { get; set; } = "";
    [MaxLength(1000)] public string Notes { get; set; } = "";
    [MaxLength(30)] public string Status { get; set; } = "New";
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

public class AccessRequestInput
{
    [Required(ErrorMessage = "اسم النشاط مطلوب"), MaxLength(120)]
    public string BusinessName { get; set; } = "";
    [Required(ErrorMessage = "اسم المسؤول مطلوب"), MaxLength(120)]
    public string ContactName { get; set; } = "";
    [Required(ErrorMessage = "رقم التواصل مطلوب"), MaxLength(40)]
    public string Phone { get; set; } = "";
    [EmailAddress(ErrorMessage = "البريد الإلكتروني غير صحيح"), MaxLength(160)]
    public string? Email { get; set; }
    [MaxLength(1000)] public string? Notes { get; set; }
    public bool Inventory { get; set; }
    public bool WhatsApp { get; set; }
    public bool Storefront { get; set; }
    public bool WebsiteAssistant { get; set; }
}
