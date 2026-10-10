namespace CypherCommerce.Services;

// One server-resolved business per request. A missing scope deliberately matches no rows.
public sealed class BusinessScope
{
    public int StoreId { get; private set; }
    public void Set(int storeId)
    {
        if (storeId <= 0 || (StoreId != 0 && StoreId != storeId))
            throw new InvalidOperationException("Invalid business scope.");
        StoreId = storeId;
    }
}
