using System.Security.Claims;
using System.Security.Cryptography;
using CypherCommerce.Data;
using CypherCommerce.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity;
using CypherCommerce.Services;
using System.Data;

namespace CypherCommerce.Controllers;

[Route("platform")]
public class PlatformController(CommerceDbContext db) : Controller
{
    [HttpGet("")]
    [HttpGet("/")]
    public IActionResult Index() => View();

    [HttpGet("request")]
    public IActionResult RequestAccess() => View(new AccessRequestInput());

    [HttpPost("request"), ValidateAntiForgeryToken]
    public async Task<IActionResult> RequestAccess(AccessRequestInput input, string? companyWebsite)
    {
        // A hidden field catches simple automated submissions without revealing which check fired.
        if (!string.IsNullOrWhiteSpace(companyWebsite)) return RedirectToAction(nameof(Thanks));
        var selected = new List<string>();
        if (input.Inventory) selected.Add("inventory");
        if (input.WhatsApp) selected.Add("whatsapp");
        if (input.Storefront) selected.Add("storefront");
        if (input.WebsiteAssistant) selected.Add("website-assistant");
        if (selected.Count == 0)
            ModelState.AddModelError("", "اختر خدمة واحدة على الأقل.");
        if (!ModelState.IsValid) return View(input);

        db.AccessRequests.Add(new AccessRequest
        {
            BusinessName = input.BusinessName.Trim(),
            ContactName = input.ContactName.Trim(),
            Phone = input.Phone.Trim(),
            Email = input.Email?.Trim() ?? "",
            RequestedServices = string.Join(',', selected),
            Notes = input.Notes?.Trim() ?? ""
        });
        await db.SaveChangesAsync();
        return RedirectToAction(nameof(Thanks));
    }

    [HttpGet("request/received")]
    public IActionResult Thanks() => View();

    [HttpGet("operator/login")]
    public IActionResult OperatorLogin() => User.IsInRole("PlatformOperator")
        ? RedirectToAction(nameof(Requests)) : View();

    [HttpPost("operator/login"), ValidateAntiForgeryToken]
    public async Task<IActionResult> OperatorLogin(string username, string password)
    {
        var account = await db.PlatformOperators.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Username == username && x.IsActive);
        byte[]? secret = null;
        try { if (password?.Length == 64) secret = Convert.FromHexString(password); }
        catch (FormatException) { }
        if (account is null || secret is null || secret.Length != 32 || account.SecretHash.Length != 32 ||
            !CryptographicOperations.FixedTimeEquals(SHA256.HashData(secret), account.SecretHash))
        {
            ModelState.AddModelError("", "بيانات الدخول غير صحيحة.");
            return View();
        }
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.Name, account.Username),
            new Claim(ClaimTypes.Role, "PlatformOperator"),
            new Claim("PlatformOperatorId", account.Id.ToString())
        ], CookieAuthenticationDefaults.AuthenticationScheme));
        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal);
        return RedirectToAction(nameof(Requests));
    }

    [Authorize(Roles = "PlatformOperator"), HttpGet("operator/requests")]
    public async Task<IActionResult> Requests()
    {
        ViewBag.PendingServiceChanges = await db.ServiceChangeRequests.IgnoreQueryFilters()
            .CountAsync(x => x.Status == "Pending");
        return View(await db.AccessRequests.AsNoTracking()
            .OrderByDescending(x => x.CreatedAtUtc).Take(200).ToListAsync());
    }

    [Authorize(Roles = "PlatformOperator"), HttpGet("operator/businesses")]
    public async Task<IActionResult> Businesses() => View(await db.StoreProfiles.AsNoTracking()
        .OrderBy(x => x.Name).ToListAsync());

    [Authorize(Roles = "PlatformOperator"), HttpGet("operator/businesses/{id:int}/services")]
    public async Task<IActionResult> ManageServices(int id)
    {
        var store = await db.StoreProfiles.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        if (store is null) return NotFound();
        ViewBag.Requests = await db.ServiceChangeRequests.IgnoreQueryFilters().AsNoTracking()
            .Where(x => x.StoreId == id).OrderByDescending(x => x.CreatedAtUtc).Take(20).ToListAsync();
        return View(store);
    }

    [Authorize(Roles = "PlatformOperator"), HttpPost("operator/businesses/{id:int}/services"), ValidateAntiForgeryToken]
    public async Task<IActionResult> ManageServices(int id, ServiceSelectionInput input)
    {
        var store = await db.StoreProfiles.FirstOrDefaultAsync(x => x.Id == id);
        if (store is null) return NotFound();
        var wasWhatsAppEnabled = store.WhatsAppEnabled;
        try { BusinessServices.ApplySelection(store, input); }
        catch (InvalidOperationException ex)
        {
            TempData["Error"] = ex.Message;
            return RedirectToAction(nameof(ManageServices), new { id });
        }
        await using var transaction = await db.Database.BeginTransactionAsync();
        if (wasWhatsAppEnabled && !store.WhatsAppEnabled) await DisconnectWhatsAppAsync(id);
        var pending = await db.ServiceChangeRequests.IgnoreQueryFilters()
            .Where(x => x.StoreId == id && x.Status == "Pending").ToListAsync();
        foreach (var request in pending)
        {
            if (BusinessServices.IsEnabled(store, request.ServiceCode) == (request.RequestedAction == "Add"))
            {
                request.Status = "Approved";
                request.OperatorNote = "تم تحديث الخدمة مباشرةً من فريق Cypher.";
                request.ResolvedAtUtc = DateTime.UtcNow;
                request.ResolvedByOperatorId = CurrentOperatorId();
            }
        }
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        TempData["Success"] = "تم تحديث خدمات النشاط.";
        return RedirectToAction(nameof(ManageServices), new { id });
    }

    [Authorize(Roles = "PlatformOperator"), HttpGet("operator/service-requests")]
    public async Task<IActionResult> ServiceRequests() => View(await db.ServiceChangeRequests
        .IgnoreQueryFilters().AsNoTracking().Include(x => x.Store)
        .OrderBy(x => x.Status == "Pending" ? 0 : 1).ThenByDescending(x => x.CreatedAtUtc)
        .Take(200).ToListAsync());

    [Authorize(Roles = "PlatformOperator"), HttpPost("operator/service-requests/{id:int}/approve"), ValidateAntiForgeryToken]
    public async Task<IActionResult> ApproveServiceRequest(int id, string? operatorNote)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        var request = await db.ServiceChangeRequests.IgnoreQueryFilters().Include(x => x.Store)
            .FirstOrDefaultAsync(x => x.Id == id && x.Status == "Pending");
        if (request is null) return NotFound();
        var wasWhatsAppEnabled = request.Store.WhatsAppEnabled;
        try { BusinessServices.ApplyRequest(request.Store, request.ServiceCode, request.RequestedAction == "Add"); }
        catch (InvalidOperationException ex)
        {
            TempData["Error"] = ex.Message;
            return RedirectToAction(nameof(ServiceRequests));
        }
        if (wasWhatsAppEnabled && !request.Store.WhatsAppEnabled)
            await DisconnectWhatsAppAsync(request.StoreId);
        request.Status = "Approved";
        request.OperatorNote = CleanOperatorNote(operatorNote);
        request.ResolvedAtUtc = DateTime.UtcNow;
        request.ResolvedByOperatorId = CurrentOperatorId();
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        TempData["Success"] = "تمت الموافقة وتحديث خدمات النشاط.";
        return RedirectToAction(nameof(ServiceRequests));
    }

    [Authorize(Roles = "PlatformOperator"), HttpPost("operator/service-requests/{id:int}/reject"), ValidateAntiForgeryToken]
    public async Task<IActionResult> RejectServiceRequest(int id, string? operatorNote)
    {
        var request = await db.ServiceChangeRequests.IgnoreQueryFilters()
            .FirstOrDefaultAsync(x => x.Id == id && x.Status == "Pending");
        if (request is null) return NotFound();
        request.Status = "Rejected";
        request.OperatorNote = CleanOperatorNote(operatorNote);
        request.ResolvedAtUtc = DateTime.UtcNow;
        request.ResolvedByOperatorId = CurrentOperatorId();
        await db.SaveChangesAsync();
        TempData["Success"] = "تم رفض طلب تغيير الخدمة.";
        return RedirectToAction(nameof(ServiceRequests));
    }

    private Task DisconnectWhatsAppAsync(int storeId) => db.Database.ExecuteSqlInterpolatedAsync(
        $"IF OBJECT_ID(N'dbo.BotWhatsAppConnections',N'U') IS NOT NULL UPDATE dbo.BotWhatsAppConnections SET Status=N'disconnected',VerifiedAtUtc=NULL WHERE StoreId={storeId}");

    private int? CurrentOperatorId() => int.TryParse(User.FindFirst("PlatformOperatorId")?.Value, out var id) ? id : null;
    private static string CleanOperatorNote(string? note)
    {
        var clean = note?.Trim() ?? "";
        return clean.Length <= 500 ? clean : clean[..500];
    }

    [Authorize(Roles = "PlatformOperator"), HttpGet("operator/requests/{id:int}/provision")]
    public async Task<IActionResult> Provision(int id)
    {
        var request = await db.AccessRequests.FindAsync(id);
        if (request is null || request.Status != "Contacted") return NotFound();
        return View(new ProvisionBusinessInput
        {
            RequestId = id,
            BusinessName = request.BusinessName,
            Inventory = request.RequestedServices.Contains("inventory"),
            WhatsApp = request.RequestedServices.Contains("whatsapp"),
            Storefront = request.RequestedServices.Contains("storefront"),
            WebsiteAssistant = request.RequestedServices.Contains("website-assistant")
        });
    }

    [Authorize(Roles = "PlatformOperator"), HttpPost("operator/requests/{id:int}/provision"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Provision(int id, ProvisionBusinessInput input)
    {
        var request = await db.AccessRequests.FindAsync(id);
        if (request is null || request.Status != "Contacted" || input.RequestId != id) return NotFound();
        if (!input.Inventory && !input.WhatsApp && !input.Storefront)
            ModelState.AddModelError("", "اختر خدمة أساسية واحدة على الأقل.");
        if (input.WebsiteAssistant && !input.Storefront)
            ModelState.AddModelError(nameof(input.WebsiteAssistant), "مساعد الموقع يتطلب تفعيل المتجر.");
        if (await db.StoreProfiles.AnyAsync(x => x.Slug == input.Slug))
            ModelState.AddModelError(nameof(input.Slug), "رابط النشاط مستخدم مسبقاً.");
        if (await db.OwnerAccounts.AnyAsync(x => x.Username == input.Username))
            ModelState.AddModelError(nameof(input.Username), "اسم المستخدم مستخدم مسبقاً.");
        if (!ModelState.IsValid) return View(input);

        await using var transaction = await db.Database.BeginTransactionAsync();
        var store = new StoreProfile
        {
            Name = input.BusinessName.Trim(), Slug = input.Slug.Trim(),
            Phone = request.Phone, InventoryEnabled = input.Inventory,
            WhatsAppEnabled = input.WhatsApp, StorefrontEnabled = input.Storefront,
            WebsiteAssistantEnabled = input.WebsiteAssistant
        };
        db.StoreProfiles.Add(store);
        await db.SaveChangesAsync();
        var account = new OwnerAccount { StoreId = store.Id, Username = input.Username.Trim() };
        account.PasswordHash = new PasswordHasher<OwnerAccount>().HashPassword(account, input.TemporaryPassword);
        db.OwnerAccounts.Add(account);
        request.Status = "Provisioned";
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        TempData["Success"] = "تم إنشاء النشاط والحساب. شارك بيانات الدخول مع صاحب النشاط عبر قناة آمنة.";
        return RedirectToAction(nameof(Businesses));
    }

    [Authorize(Roles = "PlatformOperator"), HttpPost("operator/requests/{id:int}/contacted"), ValidateAntiForgeryToken]
    public async Task<IActionResult> MarkContacted(int id)
    {
        var request = await db.AccessRequests.FindAsync(id);
        if (request is null) return NotFound();
        request.Status = "Contacted";
        await db.SaveChangesAsync();
        return RedirectToAction(nameof(Requests));
    }

    [Authorize(Roles = "PlatformOperator"), HttpPost("operator/logout"), ValidateAntiForgeryToken]
    public async Task<IActionResult> OperatorLogout()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return RedirectToAction(nameof(Index));
    }

}
