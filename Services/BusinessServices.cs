using CypherCommerce.Models;

namespace CypherCommerce.Services;

public static class BusinessServices
{
    public const string Inventory = "inventory";
    public const string WhatsApp = "whatsapp";
    public const string Storefront = "storefront";
    public const string WebsiteAssistant = "website-assistant";

    public static readonly (string Code, string Label)[] All =
    [
        (Inventory, "المنتجات والمخزون"),
        (WhatsApp, "مساعد واتساب"),
        (Storefront, "المتجر الإلكتروني"),
        (WebsiteAssistant, "مساعد الموقع")
    ];

    public static bool IsKnown(string code) => All.Any(x => x.Code == code);
    public static string Label(string code) => All.FirstOrDefault(x => x.Code == code).Label ?? code;

    public static bool IsEnabled(StoreProfile store, string code) => code switch
    {
        Inventory => store.InventoryEnabled,
        WhatsApp => store.WhatsAppEnabled,
        Storefront => store.StorefrontEnabled,
        WebsiteAssistant => store.WebsiteAssistantEnabled,
        _ => throw new InvalidOperationException("الخدمة غير معروفة.")
    };

    public static void ApplyRequest(StoreProfile store, string code, bool enable)
    {
        if (IsEnabled(store, code) == enable)
            throw new InvalidOperationException("حالة هذه الخدمة تغيّرت منذ إرسال الطلب. راجع الخدمات الحالية.");
        switch (code)
        {
            case Inventory: store.InventoryEnabled = enable; break;
            case WhatsApp: store.WhatsAppEnabled = enable; break;
            case Storefront:
                store.StorefrontEnabled = enable;
                if (!enable) store.WebsiteAssistantEnabled = false;
                break;
            case WebsiteAssistant:
                if (enable && !store.StorefrontEnabled)
                    throw new InvalidOperationException("مساعد الموقع يتطلب تفعيل المتجر الإلكتروني أولاً.");
                store.WebsiteAssistantEnabled = enable;
                break;
            default: throw new InvalidOperationException("الخدمة غير معروفة.");
        }
    }

    public static void ApplySelection(StoreProfile store, ServiceSelectionInput input)
    {
        if (input.WebsiteAssistant && !input.Storefront)
            throw new InvalidOperationException("مساعد الموقع يتطلب تفعيل المتجر الإلكتروني.");
        store.InventoryEnabled = input.Inventory;
        store.WhatsAppEnabled = input.WhatsApp;
        store.StorefrontEnabled = input.Storefront;
        store.WebsiteAssistantEnabled = input.WebsiteAssistant;
    }
}
