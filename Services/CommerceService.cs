using System.Data;
using CypherCommerce.Data;
using CypherCommerce.Models;
using Microsoft.EntityFrameworkCore;

namespace CypherCommerce.Services;

public class CommerceService(CommerceDbContext db, BusinessScope scope)
{
    public async Task<StoreProfile> GetStoreAsync() =>
        await db.StoreProfiles.FirstAsync(x => x.Id == scope.StoreId && x.IsActive);

    public async Task<ShopOrder> PlaceWebsiteOrderAsync(
        IReadOnlyDictionary<int, int> cart, CheckoutInput input)
    {
        if (cart.Count == 0) throw new InvalidOperationException("السلة فارغة.");
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        var products = await db.Products.Where(x => cart.Keys.Contains(x.Id) && x.IsActive
            && x.SubCategory != null && x.SubCategory.IsActive && x.SubCategory.ParentCategory.IsActive).ToListAsync();
        if (products.Count != cart.Count) throw new InvalidOperationException("بعض المنتجات لم تعد متاحة.");

        var store = await GetStoreAsync();
        var order = new ShopOrder
        {
            OrderNumber = NewOrderNumber(),
            SourceChannel = "website",
            CustomerName = input.CustomerName.Trim(),
            Phone = input.Phone.Trim(),
            DeliveryAddress = input.DeliveryAddress.Trim(),
            CustomerNote = input.CustomerNote?.Trim() ?? "",
            Status = OrderStatus.New,
            StockReserved = true,
            DeliveryFee = store.DeliveryFee,
            ConfirmedAtUtc = DateTime.UtcNow
        };
        foreach (var product in products)
        {
            var quantity = cart[product.Id];
            if (quantity < 1 || quantity > product.StockQuantity)
                throw new InvalidOperationException($"الكمية المطلوبة من {product.Name} غير متاحة.");
            order.Lines.Add(new OrderLine
            {
                ProductId = product.Id,
                ProductName = product.Name,
                Quantity = quantity,
                UnitPrice = product.Price,
                LineTotal = product.Price * quantity
            });
            product.StockQuantity -= quantity;
            product.UpdatedAtUtc = DateTime.UtcNow;
            db.StockMovements.Add(new StockMovement
            {
                ProductId = product.Id,
                QuantityChange = -quantity,
                Reason = $"حجز طلب {order.OrderNumber}"
            });
        }
        order.Subtotal = order.Lines.Sum(x => x.LineTotal);
        order.Total = order.Subtotal + order.DeliveryFee;
        db.Orders.Add(order);
        await db.SaveChangesAsync();
        foreach (var movement in db.ChangeTracker.Entries<StockMovement>()
                     .Where(x => x.State == EntityState.Unchanged && x.Entity.OrderId == null))
            movement.Entity.OrderId = order.Id;
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        return order;
    }

    public async Task<ShopOrder> CreateAgentDraftAsync(AgentOrderRequest input)
    {
        if (input.Lines.Count == 0) throw new InvalidOperationException("At least one order line is required.");
        var channel = input.Channel.Trim().ToLowerInvariant();
        if (channel is not ("instagram" or "whatsapp" or "facebook" or "webchat" or "other"))
            throw new InvalidOperationException("Unsupported channel.");
        var externalId = input.ExternalOrderId.Trim();
        var existing = await db.Orders.Include(x => x.Lines)
            .FirstOrDefaultAsync(x => x.SourceChannel == channel && x.ExternalOrderId == externalId);
        if (existing is not null) return existing;

        var productIds = input.Lines.Where(x => x.ProductId.HasValue).Select(x => x.ProductId!.Value).Distinct().ToList();
        var products = await db.Products.Where(x => productIds.Contains(x.Id) && x.IsActive
            && x.SubCategory != null && x.SubCategory.IsActive && x.SubCategory.ParentCategory.IsActive)
            .ToDictionaryAsync(x => x.Id);
        var order = new ShopOrder
        {
            OrderNumber = NewOrderNumber(),
            SourceChannel = channel,
            ExternalOrderId = externalId,
            ExternalConversationId = input.ExternalConversationId?.Trim(),
            CustomerName = input.CustomerName.Trim(),
            Phone = input.Phone.Trim(),
            DeliveryAddress = input.DeliveryAddress.Trim(),
            CustomerNote = input.CustomerNote.Trim(),
            Status = OrderStatus.NeedsReview,
            StockReserved = false
        };
        foreach (var line in input.Lines)
        {
            if (line.Quantity < 1) throw new InvalidOperationException("Quantity must be positive.");
            Product? product = null;
            if (line.ProductId.HasValue && !products.TryGetValue(line.ProductId.Value, out product))
                throw new InvalidOperationException($"Unknown product ID: {line.ProductId}.");
            order.Lines.Add(new OrderLine
            {
                ProductId = product?.Id,
                ProductName = product?.Name ?? line.RawCustomerText.Trim(),
                Quantity = line.Quantity,
                UnitPrice = product?.Price ?? 0,
                LineTotal = (product?.Price ?? 0) * line.Quantity,
                RawCustomerText = line.RawCustomerText.Trim(),
                AiNote = line.AiNote.Trim(),
                IsUnresolved = product is null
            });
        }
        order.Subtotal = order.Lines.Sum(x => x.LineTotal);
        order.DeliveryFee = (await GetStoreAsync()).DeliveryFee;
        order.Total = order.Subtotal + order.DeliveryFee;
        db.Orders.Add(order);
        await db.SaveChangesAsync();
        return order;
    }

    public async Task ConfirmAgentOrderAsync(int id, IReadOnlyDictionary<int, int> matches)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        var order = await db.Orders.Include(x => x.Lines).FirstOrDefaultAsync(x => x.Id == id)
            ?? throw new InvalidOperationException("الطلب غير موجود.");
        if (order.Status != OrderStatus.NeedsReview)
            throw new InvalidOperationException("تمت مراجعة هذا الطلب مسبقاً.");
        if (string.IsNullOrWhiteSpace(order.CustomerName) || string.IsNullOrWhiteSpace(order.Phone))
            throw new InvalidOperationException("الاسم ورقم الهاتف مطلوبان لتأكيد الطلب.");

        var allIds = order.Lines.Select(x => x.IsUnresolved && matches.TryGetValue(x.Id, out var mapped)
                ? mapped : x.ProductId ?? 0).Distinct().ToList();
        if (allIds.Contains(0)) throw new InvalidOperationException("اربط كل عنصر بمنتج قبل التأكيد.");
        var products = await db.Products.Where(x => allIds.Contains(x.Id) && x.IsActive
            && x.SubCategory != null && x.SubCategory.IsActive && x.SubCategory.ParentCategory.IsActive)
            .ToDictionaryAsync(x => x.Id);
        if (products.Count != allIds.Count) throw new InvalidOperationException("أحد المنتجات غير متاح.");

        var demand = order.Lines.GroupBy(x => x.IsUnresolved ? matches[x.Id] : x.ProductId!.Value)
            .ToDictionary(x => x.Key, x => x.Sum(y => y.Quantity));
        foreach (var item in demand)
            if (products[item.Key].StockQuantity < item.Value)
                throw new InvalidOperationException($"المخزون غير كافٍ للمنتج {products[item.Key].Name}.");

        foreach (var line in order.Lines)
        {
            var productId = line.IsUnresolved ? matches[line.Id] : line.ProductId!.Value;
            var product = products[productId];
            line.ProductId = product.Id;
            line.ProductName = product.Name;
            line.UnitPrice = product.Price;
            line.LineTotal = product.Price * line.Quantity;
            line.IsUnresolved = false;
            product.StockQuantity -= line.Quantity;
            product.UpdatedAtUtc = DateTime.UtcNow;
            db.StockMovements.Add(new StockMovement
            {
                ProductId = product.Id,
                QuantityChange = -line.Quantity,
                Reason = $"حجز طلب محادثة {order.OrderNumber}",
                OrderId = order.Id
            });
        }
        order.Subtotal = order.Lines.Sum(x => x.LineTotal);
        order.Total = order.Subtotal + order.DeliveryFee;
        order.Status = OrderStatus.Confirmed;
        order.StockReserved = true;
        order.ConfirmedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
    }

    public async Task ChangeStatusAsync(int id, OrderStatus newStatus)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        var order = await db.Orders.Include(x => x.Lines).FirstOrDefaultAsync(x => x.Id == id)
            ?? throw new InvalidOperationException("الطلب غير موجود.");
        if (order.Status is OrderStatus.Cancelled or OrderStatus.Completed)
            throw new InvalidOperationException("لا يمكن تغيير طلب مغلق.");
        if (newStatus == OrderStatus.NeedsReview)
            throw new InvalidOperationException("حالة غير صالحة.");
        if (order.Status == OrderStatus.NeedsReview && newStatus != OrderStatus.Cancelled)
            throw new InvalidOperationException("راجع الطلب أولاً.");
        if (newStatus == OrderStatus.Cancelled && order.StockReserved)
        {
            var productIds = order.Lines.Where(x => x.ProductId.HasValue).Select(x => x.ProductId!.Value).ToList();
            var products = await db.Products.Where(x => productIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id);
            foreach (var line in order.Lines.Where(x => x.ProductId.HasValue))
            {
                products[line.ProductId!.Value].StockQuantity += line.Quantity;
                db.StockMovements.Add(new StockMovement
                {
                    ProductId = line.ProductId.Value,
                    QuantityChange = line.Quantity,
                    Reason = $"إلغاء طلب {order.OrderNumber}",
                    OrderId = order.Id
                });
            }
            order.StockReserved = false;
        }
        order.Status = newStatus;
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
    }

    public static string NewOrderNumber() => $"CY-{DateTime.UtcNow:yyMMdd}-{Guid.NewGuid().ToString("N")[..8].ToUpperInvariant()}";
}
