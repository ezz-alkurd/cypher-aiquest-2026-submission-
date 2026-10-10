using CypherCommerce.Data;
using CypherCommerce.Services;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using CypherCommerce.Models;
using System.Security.Cryptography;

var builder = WebApplication.CreateBuilder(args);
if (builder.Environment.IsEnvironment("HostedLocal"))
    builder.Configuration.AddUserSecrets<Program>(optional: true);

builder.Services.AddControllersWithViews();
builder.Services.AddAntiforgery(options => options.HeaderName = "RequestVerificationToken");
builder.Services.AddHttpClient("cypher-assistant", client => client.Timeout = TimeSpan.FromSeconds(100))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("website-assistant", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new FixedWindowRateLimiterOptions
        { PermitLimit = 20, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    options.AddPolicy("order-tracking", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new FixedWindowRateLimiterOptions
        { PermitLimit = 10, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});
builder.Services.AddDistributedMemoryCache();
builder.Services.AddSession(options =>
{
    options.IdleTimeout = TimeSpan.FromDays(7);
    options.Cookie.HttpOnly = true;
    options.Cookie.IsEssential = true;
    options.Cookie.SameSite = SameSiteMode.Lax;
});
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/admin/login";
        options.AccessDeniedPath = "/admin/login";
        options.Cookie.Name = "CypherCommerce.Admin";
        options.Cookie.HttpOnly = true;
        options.ExpireTimeSpan = TimeSpan.FromHours(10);
        options.Events.OnRedirectToLogin = context =>
        {
            var path = context.Request.Path.StartsWithSegments("/platform/operator")
                ? "/platform/operator/login" : "/admin/login";
            context.Response.Redirect(path);
            return Task.CompletedTask;
        };
        options.Events.OnRedirectToAccessDenied = context =>
        {
            var path = context.Request.Path.StartsWithSegments("/platform/operator")
                ? "/platform/operator/login" : "/admin/login";
            context.Response.Redirect(path);
            return Task.CompletedTask;
        };
        options.Events.OnValidatePrincipal = async context =>
        {
            if (context.Principal?.IsInRole("StoreOwner") == true &&
                (!int.TryParse(context.Principal.FindFirst("OwnerAccountId")?.Value, out _) ||
                 !int.TryParse(context.Principal.FindFirst("StoreId")?.Value, out _)))
                context.RejectPrincipal();
            if (context.Principal?.IsInRole("PlatformOperator") == true)
            {
                var operatorId = int.TryParse(context.Principal.FindFirst("PlatformOperatorId")?.Value, out var id) ? id : 0;
                var db = context.HttpContext.RequestServices.GetRequiredService<CommerceDbContext>();
                if (operatorId == 0 || !await db.PlatformOperators.AnyAsync(x => x.Id == operatorId && x.IsActive))
                    context.RejectPrincipal();
            }
        };
    });
builder.Services.AddAuthorization();

var connectionString = builder.Configuration.GetConnectionString("CommerceDb")
    ?? throw new InvalidOperationException("ConnectionStrings:CommerceDb must be configured.");
var sqlConnection = new SqlConnectionStringBuilder(connectionString);
if (!sqlConnection.IntegratedSecurity && string.IsNullOrWhiteSpace(sqlConnection.Password))
{
    var databasePassword = builder.Configuration["ConnectionStrings:CommerceDbPassword"];
    if (string.IsNullOrWhiteSpace(databasePassword))
        throw new InvalidOperationException(
            "The SQL password is missing. In Visual Studio, right-click the project, select Manage User Secrets, and set ConnectionStrings:CommerceDbPassword.");
    sqlConnection.Password = databasePassword;
}
builder.Services.AddDbContext<CommerceDbContext>(options => options.UseSqlServer(sqlConnection.ConnectionString));
builder.Services.AddScoped<BusinessScope>();
builder.Services.AddScoped<CommerceService>();
builder.Services.AddHostedService<BotRecoveryService>();

var app = builder.Build();

if (args.Contains("--create-demo-store", StringComparer.OrdinalIgnoreCase))
{
    if (!(app.Environment.IsDevelopment() || app.Environment.IsEnvironment("HostedLocal")))
        throw new InvalidOperationException("Demo setup is limited to Development or HostedLocal.");
    await DemoStoreSetup.CreateAsync(app.Services, app.Configuration);
    return;
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();
app.UseSession();
app.UseAuthentication();
app.Use(async (context, next) =>
{
    var path = context.Request.Path.Value ?? "";
    var business = context.RequestServices.GetRequiredService<BusinessScope>();
    var db = context.RequestServices.GetRequiredService<CommerceDbContext>();
    if (path.StartsWith("/admin", StringComparison.OrdinalIgnoreCase) &&
        !path.Equals("/admin/login", StringComparison.OrdinalIgnoreCase))
    {
        var accountId = int.TryParse(context.User.FindFirst("OwnerAccountId")?.Value, out var id) ? id : 0;
        var storeId = int.TryParse(context.User.FindFirst("StoreId")?.Value, out var claimed) ? claimed : 0;
        if (accountId > 0 && storeId > 0 && await db.OwnerAccounts.AnyAsync(x => x.Id == accountId &&
                x.StoreId == storeId && x.IsActive) &&
            await db.StoreProfiles.AnyAsync(x => x.Id == storeId && x.IsActive))
            business.Set(storeId);
        else if (context.User.IsInRole("StoreOwner"))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }
    }
    else if (path.StartsWith("/s/", StringComparison.OrdinalIgnoreCase))
    {
        var slug = path.Split('/', StringSplitOptions.RemoveEmptyEntries).ElementAtOrDefault(1);
        var store = await db.StoreProfiles.AsNoTracking().FirstOrDefaultAsync(x => x.Slug == slug && x.IsActive && x.StorefrontEnabled);
        if (store is null) { context.Response.StatusCode = 404; return; }
        business.Set(store.Id);
        context.Items["StoreBasePath"] = $"/s/{store.Slug}";
    }
    else if (path.StartsWith("/api/v1/agent", StringComparison.OrdinalIgnoreCase) && !app.Environment.IsDevelopment())
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }
    else if (path.Equals("/shop", StringComparison.OrdinalIgnoreCase) ||
             path.StartsWith("/products", StringComparison.OrdinalIgnoreCase) ||
             path.StartsWith("/cart", StringComparison.OrdinalIgnoreCase) ||
             path.StartsWith("/checkout", StringComparison.OrdinalIgnoreCase) ||
             path.StartsWith("/order/", StringComparison.OrdinalIgnoreCase) ||
             path.Equals("/track", StringComparison.OrdinalIgnoreCase) ||
             (app.Environment.IsDevelopment() && path.StartsWith("/api/v1/agent", StringComparison.OrdinalIgnoreCase)))
    {
        var pilotId = app.Configuration.GetValue<int>("Platform:LegacyStoreId");
        if (pilotId == 0 && app.Environment.IsDevelopment()) pilotId = 1;
        if (pilotId > 0 && await db.StoreProfiles.AnyAsync(x => x.Id == pilotId && x.IsActive && x.StorefrontEnabled)) business.Set(pilotId);
        else { context.Response.StatusCode = 404; return; }
    }
    await next();
});
app.UseAuthorization();
app.UseRateLimiter();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Store}/{action=Index}/{id?}");

if (args.Contains("--check-database", StringComparer.OrdinalIgnoreCase))
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<CommerceDbContext>();
    await db.Database.OpenConnectionAsync();
    var connection = db.Database.GetDbConnection();
    Console.WriteLine($"Connected to SQL Server: {connection.DataSource} / {connection.Database}");
    await db.Database.CloseConnectionAsync();
    return;
}

if (args.Contains("--check-integration", StringComparer.OrdinalIgnoreCase))
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<CommerceDbContext>();
    await db.Database.OpenConnectionAsync();
    try
    {
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = """
            SELECT
              DB_NAME(),
              OBJECT_ID(N'dbo.StoreProfiles', N'U'),
              OBJECT_ID(N'dbo.PlatformOperators', N'U'),
              OBJECT_ID(N'dbo.BotStoreFacts', N'U'),
              OBJECT_ID(N'dbo.BotWhatsAppConnections', N'U'),
              OBJECT_ID(N'dbo.BotWebsiteVisitors', N'U'),
              OBJECT_ID(N'dbo.BotSessions', N'U'),
              OBJECT_ID(N'dbo.BotMessages', N'U'),
              OBJECT_ID(N'dbo.BotCartOperations', N'U'),
              OBJECT_ID(N'dbo.BotDeliveries', N'U'),
              OBJECT_ID(N'dbo.BotHistory', N'U'),
              OBJECT_ID(N'dbo.BotRecoveryQueue', N'U'),
              OBJECT_ID(N'dbo.CypherRegisterWebsiteVisitor', N'P'),
              OBJECT_ID(N'dbo.CypherSweepBotRecovery', N'P'),
              OBJECT_ID(N'dbo.CypherBot', N'P'),
              (SELECT COUNT(*) FROM dbo.StoreProfiles WHERE IsActive = 1);
            """;
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) throw new InvalidOperationException("Integration check returned no result.");
        var objects = new[] { "StoreProfiles", "PlatformOperators", "BotStoreFacts", "BotWhatsAppConnections",
            "BotWebsiteVisitors", "BotSessions", "BotMessages", "BotCartOperations", "BotDeliveries",
            "BotHistory", "BotRecoveryQueue", "CypherRegisterWebsiteVisitor", "CypherSweepBotRecovery", "CypherBot" };
        Console.WriteLine($"Database: {reader.GetString(0)}; active stores: {reader.GetInt32(15)}");
        for (var i = 0; i < objects.Length; i++)
            Console.WriteLine($"{objects[i]}: {(reader.IsDBNull(i + 1) ? "missing" : "installed")}");
        await reader.DisposeAsync();
        command.CommandText = """
            SELECT s.Id, s.Slug, s.StorefrontEnabled, s.WebsiteAssistantEnabled, s.WhatsAppEnabled,
              (SELECT COUNT(*) FROM dbo.Products p WHERE p.StoreId = s.Id AND p.IsActive = 1),
              (SELECT COUNT(*) FROM dbo.OwnerAccounts o WHERE o.StoreId = s.Id AND o.IsActive = 1)
            FROM dbo.StoreProfiles s WHERE s.IsActive = 1 ORDER BY s.Id;
            """;
        await using var stores = await command.ExecuteReaderAsync();
        while (await stores.ReadAsync())
            Console.WriteLine($"Store {stores.GetInt32(0)}: slug={stores.GetString(1)}, storefront={stores.GetBoolean(2)}, " +
                $"website assistant={stores.GetBoolean(3)}, WhatsApp={stores.GetBoolean(4)}, " +
                $"active products={stores.GetInt32(5)}, active owners={stores.GetInt32(6)}");
        await stores.DisposeAsync();
        command.CommandText = """
            SELECT s.StoreId, s.InstanceName, s.Status,
              CASE WHEN s.VerifiedAtUtc IS NULL THEN 0 ELSE 1 END
            FROM dbo.BotWhatsAppConnections s ORDER BY s.StoreId;
            """;
        await using var connections = await command.ExecuteReaderAsync();
        while (await connections.ReadAsync())
            Console.WriteLine($"WhatsApp store {connections.GetInt32(0)}: instance={connections.GetString(1)}, " +
                $"status={connections.GetString(2)}, verified={connections.GetInt32(3) == 1}");
    }
    finally { await db.Database.CloseConnectionAsync(); }
    Console.WriteLine($"Website assistant URL configured: {!string.IsNullOrWhiteSpace(app.Configuration["Automation:WebsiteAssistantUrl"])}");
    Console.WriteLine($"Website assistant key configured: {!string.IsNullOrWhiteSpace(app.Configuration["Automation:WebsiteAssistantKey"])}");
    Console.WriteLine($"Owner setup URL configured: {!string.IsNullOrWhiteSpace(app.Configuration["Automation:OwnerSetupUrl"])}");
    Console.WriteLine($"Owner setup key configured: {!string.IsNullOrWhiteSpace(app.Configuration["Automation:OwnerSetupKey"])}");
    return;
}

if (args.Contains("--install-platform-schema", StringComparer.OrdinalIgnoreCase))
{
    using var scope = app.Services.CreateScope();
    await SchemaUpgrade.ApplyPlatformOperatorAsync(scope.ServiceProvider.GetRequiredService<CommerceDbContext>());
    Console.WriteLine("Platform operator schema is ready; no account was created.");
    return;
}

if (args.Contains("--install-service-schema", StringComparer.OrdinalIgnoreCase))
{
    using var scope = app.Services.CreateScope();
    await SchemaUpgrade.ApplyServiceChangeRequestsAsync(scope.ServiceProvider.GetRequiredService<CommerceDbContext>());
    Console.WriteLine("Service request schema is ready; no business data was changed.");
    return;
}

if (args.Contains("--create-platform-operator", StringComparer.OrdinalIgnoreCase))
{
    if (!app.Environment.IsEnvironment("HostedLocal"))
        throw new InvalidOperationException("Operator setup is allowed only from the local hosted-database profile.");
    var username = app.Configuration["Platform:OperatorUsername"]?.Trim();
    if (string.IsNullOrWhiteSpace(username) || username.Length > 100)
        throw new InvalidOperationException("Set Platform:OperatorUsername in private local configuration first.");
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<CommerceDbContext>();
    if (await db.PlatformOperators.AnyAsync(x => x.Username == username))
    {
        Console.WriteLine($"Platform operator {username} already exists; no key changed.");
        return;
    }
    var keyBytes = RandomNumberGenerator.GetBytes(32);
    db.PlatformOperators.Add(new PlatformOperator { Username = username, SecretHash = SHA256.HashData(keyBytes) });
    await db.SaveChangesAsync();
    Console.WriteLine($"Platform operator created: {username}");
    Console.WriteLine($"One-time sign-in key: {Convert.ToHexString(keyBytes)}");
    return;
}

if (args.Contains("--test-bot-sql", StringComparer.OrdinalIgnoreCase))
{
    var scriptPath = Path.Combine(app.Environment.ContentRootPath, "automation", "sql-integration.test.sql");
    if (!File.Exists(scriptPath)) throw new FileNotFoundException("Bot integration test script was not found.", scriptPath);
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<CommerceDbContext>();
    await db.Database.OpenConnectionAsync();
    try
    {
        await using var transaction = await db.Database.GetDbConnection().BeginTransactionAsync();
        try
        {
            await using var command = db.Database.GetDbConnection().CreateCommand();
            command.Transaction = transaction;
            command.CommandTimeout = 120;
            command.CommandText = await File.ReadAllTextAsync(scriptPath);
            await using var result = await command.ExecuteReaderAsync();
            int? passedAssertions = null;
            do
            {
                while (await result.ReadAsync())
                {
                    if (result.FieldCount != 1 || result.IsDBNull(0)) continue;
                    using var document = System.Text.Json.JsonDocument.Parse(result.GetString(0));
                    var root = document.RootElement;
                    if (root.TryGetProperty("stage", out var stage) && stage.GetString() == "synthetic_sql_tests"
                        && root.TryGetProperty("success", out var success) && success.GetBoolean()
                        && root.TryGetProperty("rollbackRequired", out var rollback) && rollback.GetBoolean())
                        passedAssertions = root.GetProperty("passedAssertions").GetInt32();
                }
            } while (await result.NextResultAsync());
            if (passedAssertions is null) throw new InvalidOperationException("Bot integration test did not report success and rollback.");
            Console.WriteLine($"Bot SQL assertions passed: {passedAssertions}");
        }
        finally { await transaction.RollbackAsync(); }
        Console.WriteLine("Synthetic test data rolled back.");
    }
    finally { await db.Database.CloseConnectionAsync(); }
    return;
}

if (args.Contains("--initialize-database", StringComparer.OrdinalIgnoreCase))
{
    await SeedData.InitializeAsync(app.Services, seedDevelopmentPilot: false);
    Console.WriteLine("Cypher database schema is ready.");
    return;
}
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<CommerceDbContext>();
    await db.Database.OpenConnectionAsync();
    await db.Database.CloseConnectionAsync();
}
app.Run();
