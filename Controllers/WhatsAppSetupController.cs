using System.Text.Json;
using System.Text.RegularExpressions;
using CypherCommerce.Data;
using CypherCommerce.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CypherCommerce.Controllers;

[Authorize(Roles = "StoreOwner"), Route("admin/whatsapp")]
public sealed class WhatsAppSetupController(CommerceDbContext db, BusinessScope scope,
    IHttpClientFactory clients, IConfiguration config) : Controller
{
    [HttpPost("setup"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Setup([FromBody] SetupInput input, CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";
        if (input.Action is not ("connect" or "verify")) return BadRequest(new { success = false });
        if (!int.TryParse(User.FindFirst("OwnerAccountId")?.Value, out var ownerId)) return Forbid();
        var store = await db.StoreProfiles.AsNoTracking().SingleOrDefaultAsync(x => x.Id == scope.StoreId
            && x.IsActive && x.WhatsAppEnabled, cancellationToken);
        if (store is null || !await db.OwnerAccounts.AnyAsync(x => x.Id == ownerId && x.StoreId == store.Id && x.IsActive, cancellationToken)) return Forbid();
        var phone = store.WhatsApp.Trim().TrimStart('+');
        if (!Regex.IsMatch(phone, "^[1-9][0-9]{7,14}$")) return BadRequest(new { success = false, message = "احفظ رقم واتساب بصيغة دولية أولاً." });
        var key = config["Automation:OwnerSetupKey"];
        if (string.IsNullOrWhiteSpace(key) || key.Contains('\r') || key.Contains('\n')
            || !Uri.TryCreate(config["Automation:OwnerSetupUrl"], UriKind.Absolute, out var uri)
            || uri.Scheme != "https" || uri.Host != "n8n.207-180-200-93.sslip.io" || uri.UserInfo.Length > 0
            || uri.Query.Length > 0 || uri.Fragment.Length > 0 || uri.AbsolutePath != "/webhook/cypher-store-whatsapp-v1")
            return StatusCode(503, new { success = false, message = "ربط واتساب قيد التجهيز." });
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(80));
            using var request = new HttpRequestMessage(HttpMethod.Post, uri)
            {
                Content = JsonContent.Create(new { storeId = store.Id, ownerUserId = ownerId.ToString(), action = input.Action, expectedPhone = phone })
            };
            request.Headers.Add("X-Cypher-Website-Key", key);
            using var response = await clients.CreateClient("cypher-assistant").SendAsync(request,
                HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode) return StatusCode(503, new { success = false, message = "تعذر تجهيز الربط. انتظر ثم جرّب لاحقاً." });
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var content = new MemoryStream(); var buffer = new byte[8192]; int count;
            while ((count = await stream.ReadAsync(buffer, timeout.Token)) > 0)
            {
                if (content.Length + count > 500000) throw new JsonException();
                await content.WriteAsync(buffer.AsMemory(0, count), timeout.Token);
            }
            using var document = JsonDocument.Parse(content.ToArray()); var result = document.RootElement;
            if (!result.TryGetProperty("success", out var success) || success.ValueKind != JsonValueKind.True)
                return StatusCode(503, new { success = false, message = "تعذر التحقق من الربط." });
            var ready = result.TryGetProperty("ready", out var connected) && connected.ValueKind == JsonValueKind.True;
            string? qr = null;
            if (!ready && result.TryGetProperty("qrBase64", out var encoded) && encoded.ValueKind == JsonValueKind.String)
            {
                var value = encoded.GetString() ?? "";
                if (value.StartsWith("data:image/png;base64,", StringComparison.Ordinal)) value = value[22..];
                if (value.Length is > 0 and <= 400000 && Regex.IsMatch(value, "^[A-Za-z0-9+/=]+$")) qr = "data:image/png;base64," + value;
            }
            return Json(new { success = true, ready, qrBase64 = qr, message = ready ? "واتساب مربوط وتم التحقق من رقمك." : "امسح QR من الأجهزة المرتبطة، ثم اضغط تحقق." });
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        { return StatusCode(503, new { success = false, message = "تعذر التحقق حالياً. جرّب لاحقاً." }); }
    }

    public sealed class SetupInput { public string Action { get; set; } = ""; }
}
