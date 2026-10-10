namespace CypherCommerce.Models;

public static class OrderStatusDisplay
{
    public static string Label(OrderStatus status) => status switch
    {
        OrderStatus.NeedsReview => "بانتظار المراجعة",
        OrderStatus.New => "جديد",
        OrderStatus.Confirmed => "تم التأكيد",
        OrderStatus.Preparing => "قيد التجهيز",
        OrderStatus.Ready => "جاهز",
        OrderStatus.Completed => "مكتمل",
        OrderStatus.Cancelled => "ملغى",
        _ => status.ToString()
    };
}
