using System.ComponentModel.DataAnnotations;
using System.Data;
using System.Net;
using System.Text.Json;
using CypherCommerce.Data;
using CypherCommerce.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace CypherCommerce.Controllers;

[Route("s/{slug}/assistant")]
[EnableRateLimiting("website-assistant")]
public sealed class AssistantController(CommerceDbContext db, BusinessScope scope,
    IHttpClientFactory clients, IConfiguration config) : Controller
{
    public sealed class MessageInput
    {
        [Required, StringLength(2000, MinimumLength = 1)] public string Text { get; set; } = "";
        [Required, RegularExpression("^[A-Za-z0-9_-]{16,128}$")] public string RequestId { get; set; } = "";
    }

    [HttpPost, ValidateAntiForgeryToken, RequestSizeLimit(12000)]
    public async Task<IActionResult> Send(string slug, [FromBody] MessageInput input, CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";
        if (!ModelState.IsValid || string.IsNullOrWhiteSpace(input.Text))
            return StatusCode(400, Error("validation_error", "اكتب سؤالاً من ١ إلى ٢٠٠٠ حرف."));
        var store = await db.StoreProfiles.AsNoTracking().FirstOrDefaultAsync(x => x.Id == scope.StoreId
            && x.Slug == slug && x.IsActive && x.StorefrontEnabled && x.WebsiteAssistantEnabled, cancellationToken);
        if (store is null) return NotFound(Error("assistant_disabled", "المساعد غير متاح لهذا المتجر."));
        var headerValue = config["Automation:WebsiteAssistantKey"];
        var url = config["Automation:WebsiteAssistantUrl"];
        if (string.IsNullOrWhiteSpace(headerValue) || headerValue.Contains('\r') || headerValue.Contains('\n')
            || !Uri.TryCreate(url, UriKind.Absolute, out var webhook)
            || webhook.Scheme != "https" || webhook.Host != "n8n.207-180-200-93.sslip.io"
            || webhook.UserInfo.Length != 0 || webhook.Query.Length != 0 || webhook.Fragment.Length != 0
            || webhook.AbsolutePath is not ("/webhook/cypher-website-assistant-v1" or "/webhook-test/cypher-website-assistant-v1"))
            return StatusCode(503, Error("integration_unavailable", "المساعد قيد التجهيز، جرّب لاحقاً."));

        // Visitor identity is server-generated and isolated by store; frontend IDs are ignored.
        var visitorKey = $"assistant-visitor:{store.Id}";
        var visitorId = HttpContext.Session.GetString(visitorKey);
        if (visitorId is null)
        {
            visitorId = Guid.NewGuid().ToString("N");
            HttpContext.Session.SetString(visitorKey, visitorId);
        }
        try
        {
            // Registration is an app-side trust boundary, not an AI tool.
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"EXEC dbo.CypherRegisterWebsiteVisitor @storeId={store.Id}, @visitorId={visitorId}", cancellationToken);
            using var request = new HttpRequestMessage(HttpMethod.Post, webhook)
            {
                Content = JsonContent.Create(new { storefrontSlug = store.Slug, visitorSessionId = visitorId,
                    requestId = input.RequestId, text = input.Text.Trim() })
            };
            request.Headers.Add("X-Cypher-Website-Key", headerValue);
            // A disconnected browser does not cancel an accepted durable n8n turn.
            using var turnTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(100));
            using var response = await clients.CreateClient("cypher-assistant").SendAsync(request,
                HttpCompletionOption.ResponseHeadersRead, turnTimeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                var status = (int)response.StatusCode;
                return StatusCode(status is 409 or 429 ? status : 503, Error(status == 409 ? "conversation_busy"
                    : status == 429 ? "rate_limit_exceeded" : "integration_unavailable",
                    status == 409 ? "في رسالة قيد المعالجة؛ انتظر شوي وجرّب نفس الرسالة."
                    : status == 429 ? "خفف الرسائل شوي وجرّب لاحقاً." : "تعذر الوصول للمساعد، جرّب نفس الرسالة بعد شوي."));
            }
            using var bounded = new MemoryStream();
            await using var stream = await response.Content.ReadAsStreamAsync(turnTimeout.Token);
            var buffer = new byte[8192];
            int read;
            while ((read = await stream.ReadAsync(buffer, turnTimeout.Token)) > 0)
            {
                if (bounded.Length + read > 200000) return StatusCode(503, Error("invalid_response", "تعذر عرض الرد."));
                await bounded.WriteAsync(buffer.AsMemory(0, read), CancellationToken.None);
            }
            using var document = JsonDocument.Parse(bounded.ToArray());
            var root = document.RootElement;
            if (!root.TryGetProperty("success", out var success) || success.ValueKind != JsonValueKind.True
                || !root.TryGetProperty("requestId", out var requestId) || requestId.GetString() != input.RequestId
                || !root.TryGetProperty("reply", out var reply) || reply.ValueKind != JsonValueKind.String
                || (reply.GetString()?.Length ?? 0) > 6000
                || !root.TryGetProperty("products", out var products) || products.ValueKind != JsonValueKind.Array
                || products.GetArrayLength() > 20)
                return StatusCode(503, Error("invalid_response", "تعذر التحقق من الرد."));
            var ids = products.EnumerateArray().Select(x => x.GetProperty("productId").GetInt32()).Distinct().ToArray();
            // Rebuild every card from this storefront's public catalog; never trust returned URLs or tenant IDs.
            var publicProducts = await db.Products.AsNoTracking().Where(x => ids.Contains(x.Id) && x.IsActive
                && x.SubCategory != null && x.SubCategory.StoreId == store.Id && x.SubCategory.IsActive
                && x.SubCategory.ParentCategory.StoreId == store.Id && x.SubCategory.ParentCategory.IsActive)
                .ToDictionaryAsync(x => x.Id, cancellationToken);
            if (ids.Any(id => !publicProducts.ContainsKey(id)))
                return StatusCode(409, Error("catalog_changed", "تغير الكتالوج؛ ابعث سؤالاً جديداً للتحقق."));
            foreach (var card in products.EnumerateArray())
            {
                var product = publicProducts[card.GetProperty("productId").GetInt32()];
                if (!card.TryGetProperty("priceCents", out var price) || !price.TryGetInt32(out var cents)
                    || cents != decimal.ToInt32(product.Price * 100)
                    || !card.TryGetProperty("available", out var available)
                    || available.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
                    || available.GetBoolean() != (product.StockQuantity > 0))
                    return StatusCode(409, Error("catalog_changed", "تغير السعر أو التوفر؛ ابعث سؤالاً جديداً للتحقق."));
            }
            var cards = products.EnumerateArray().Select(card => publicProducts[card.GetProperty("productId").GetInt32()])
                .Select(p => new { productId = p.Id, name = p.Name, category = p.Category,
                    description = p.Description, unit = p.Unit, priceCents = decimal.ToInt32(p.Price * 100),
                    unitPrice = p.Price.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture),
                    currency = "JOD", available = p.StockQuantity > 0,
                    imageUrl = SafeImage(p.ImageUrl), productUrl = $"/s/{store.Slug}/products/{p.Id}" }).ToArray();
            return Json(new { success = true, requestId = input.RequestId, reply = reply.GetString(), products = cards,
                hasMore = root.TryGetProperty("catalog", out var catalog) && catalog.ValueKind == JsonValueKind.Object
                    && catalog.TryGetProperty("hasMore", out var more) && more.ValueKind == JsonValueKind.True });
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException
            or InvalidOperationException or KeyNotFoundException or FormatException or System.Data.Common.DbException)
        {
            // No provider error body, SQL text, secret, or customer transcript is returned to the browser.
            return StatusCode(503, Error("integration_unavailable", "ما قدرت أتحقق حالياً. جرّب نفس الرسالة بعد شوي."));
        }
    }

    private string? SafeImage(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 2000 || value.Any(char.IsWhiteSpace)
            || value.Contains('\\') || value.Contains('%') || value.Contains('?') || value.Contains('#')
            || value.Split('/').Any(segment => segment is "." or "..")) return null;
        if (value.StartsWith("/uploads/", StringComparison.Ordinal) || value.StartsWith("/images/", StringComparison.Ordinal)) return value;
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == "https" && uri.UserInfo.Length == 0
            && config.GetSection("Automation:PublicImageHosts").Get<string[]>()?.Contains(uri.Host, StringComparer.OrdinalIgnoreCase) == true)
            return uri.AbsoluteUri;
        return null;
    }

    private static object Error(string error, string message) => new { success = false, error, message };
}
