using System.Reflection;
using Microsoft.EntityFrameworkCore;

namespace CypherCommerce.Data;

public static class SchemaUpgrade
{
    public static async Task ApplyServiceChangeRequestsAsync(CommerceDbContext db)
    {
        const string resourceName = "CypherCommerce.Database.007_service_change_requests.sql";
        await using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Missing database script: {resourceName}");
        using var reader = new StreamReader(stream);
        await db.Database.ExecuteSqlRawAsync(await reader.ReadToEndAsync());
    }

    public static async Task ApplyPlatformOperatorAsync(CommerceDbContext db)
    {
        const string resourceName = "CypherCommerce.Database.004_platform_operators.sql";
        await using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Missing database script: {resourceName}");
        using var reader = new StreamReader(stream);
        await db.Database.ExecuteSqlRawAsync(await reader.ReadToEndAsync());
    }

    public static async Task ApplyAsync(CommerceDbContext db)
    {
        foreach (var resourceName in new[]
                 {
                     "CypherCommerce.Database.001_categories.sql",
                     "CypherCommerce.Database.002_access_requests.sql",
                     "CypherCommerce.Database.003_business_scope.sql",
                     "CypherCommerce.Database.004_platform_operators.sql",
                     "CypherCommerce.Database.007_service_change_requests.sql"
                 })
        {
            await using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName)
                ?? throw new InvalidOperationException($"Missing database script: {resourceName}");
            using var reader = new StreamReader(stream);
            var sql = await reader.ReadToEndAsync();
            await db.Database.ExecuteSqlRawAsync(sql);
        }
    }
}
