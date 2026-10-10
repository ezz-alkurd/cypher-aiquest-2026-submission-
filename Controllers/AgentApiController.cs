using System.Security.Cryptography;
using System.Text;
using CypherCommerce.Data;
using CypherCommerce.Models;
using CypherCommerce.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CypherCommerce.Controllers;

[ApiController]
[Route("api/v1/agent")]
public class AgentApiController(CommerceDbContext db, CommerceService commerce,
    IConfiguration configuration) : ControllerBase
{
    [HttpGet("context")]
    public async Task<IActionResult> Context()
    {
        if (!HasKey()) return Unauthorized();
        var store = await commerce.GetStoreAsync();
        var products = await db.Products.AsNoTracking().Where(x => x.IsActive
            && x.SubCategory != null && x.SubCategory.IsActive && x.SubCategory.ParentCategory.IsActive)
            .OrderBy(x => x.Name).Select(x => new
            {
                x.Id, x.Sku, x.Name, x.Description, x.Category, x.Unit,
                x.SubCategoryId, SubCategory = x.SubCategory!.Name,
                CategoryId = x.SubCategory.ParentCategoryId,
                x.Aliases, x.Price, x.StockQuantity, x.ImageUrl
            }).ToListAsync();
        return Ok(new
        {
            store = new { store.Name, store.Phone, store.WhatsApp, store.Address, store.DeliveryFee },
            currency = "JOD",
            products
        });
    }

    [HttpPost("orders")]
    public async Task<IActionResult> CreateOrder([FromBody] AgentOrderRequest request)
    {
        if (!HasKey()) return Unauthorized();
        if (!ModelState.IsValid) return ValidationProblem(ModelState);
        try
        {
            var order = await commerce.CreateAgentDraftAsync(request);
            return CreatedAtAction(nameof(GetOrder), new { id = order.Id }, ToResponse(order));
        }
        catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
    }

    [HttpGet("orders/{id:int}")]
    public async Task<IActionResult> GetOrder(int id)
    {
        if (!HasKey()) return Unauthorized();
        var order = await db.Orders.AsNoTracking().Include(x => x.Lines)
            .FirstOrDefaultAsync(x => x.Id == id);
        return order is null ? NotFound() : Ok(ToResponse(order));
    }

    private object ToResponse(ShopOrder order) => new
    {
        order.Id, order.OrderNumber,
        status = order.Status.ToString(),
        order.SourceChannel, order.ExternalOrderId,
        order.Subtotal, order.DeliveryFee, order.Total,
        lines = order.Lines.Select(x => new
        {
            x.Id, x.ProductId, x.ProductName, x.Quantity, x.UnitPrice,
            x.LineTotal, x.RawCustomerText, x.AiNote, x.IsUnresolved
        })
    };

    private bool HasKey()
    {
        var expected = configuration["Agent:IntegrationKey"];
        var actual = Request.Headers["X-Agent-Key"].ToString();
        if (string.IsNullOrWhiteSpace(expected) || string.IsNullOrWhiteSpace(actual)) return false;
        var left = SHA256.HashData(Encoding.UTF8.GetBytes(actual));
        var right = SHA256.HashData(Encoding.UTF8.GetBytes(expected));
        return CryptographicOperations.FixedTimeEquals(left, right);
    }
}
