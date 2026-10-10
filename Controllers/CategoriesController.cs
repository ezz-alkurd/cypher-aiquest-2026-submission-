using CypherCommerce.Data;
using CypherCommerce.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CypherCommerce.Controllers;

[Authorize(Roles = "StoreOwner")]
[Route("admin")]
public class CategoriesController(CommerceDbContext db) : Controller
{
    [HttpGet("categories")]
    public async Task<IActionResult> Index() => View(await db.Categories.AsNoTracking()
        .Include(x => x.SubCategories).ThenInclude(x => x.Products)
        .OrderBy(x => x.SortOrder).ThenBy(x => x.Name).ToListAsync());

    [HttpGet("categories/new")]
    public IActionResult NewCategory() => View("CategoryForm", new CategoryInput());

    [HttpGet("categories/{id:int}/edit")]
    public async Task<IActionResult> EditCategory(int id)
    {
        var category = await db.Categories.FindAsync(id);
        return category is null ? NotFound() : View("CategoryForm", new CategoryInput
        {
            Id = category.Id, Name = category.Name, Description = category.Description,
            SortOrder = category.SortOrder, IsActive = category.IsActive
        });
    }

    [HttpPost("categories/save"), ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveCategory(CategoryInput input)
    {
        if (input.Id != 0 && !await db.Categories.AnyAsync(x => x.Id == input.Id)) return NotFound();
        var name = input.Name?.Trim() ?? "";
        if (await db.Categories.AnyAsync(x => x.Name == name && x.Id != input.Id))
            ModelState.AddModelError(nameof(input.Name), "اسم التصنيف مستخدم مسبقاً.");
        if (!ModelState.IsValid) return View("CategoryForm", input);

        ProductCategory category;
        if (input.Id == 0)
        {
            category = new ProductCategory();
            db.Categories.Add(category);
        }
        else
        {
            category = await db.Categories.FindAsync(input.Id) ?? throw new InvalidOperationException("Category not found.");
        }
        category.Name = name;
        category.Description = input.Description?.Trim() ?? "";
        category.SortOrder = input.SortOrder;
        category.IsActive = input.IsActive;
        await db.SaveChangesAsync();
        await db.Products.Where(x => x.SubCategory != null && x.SubCategory.ParentCategoryId == category.Id)
            .ExecuteUpdateAsync(x => x.SetProperty(p => p.Category, category.Name));
        TempData["Success"] = "تم حفظ التصنيف الرئيسي.";
        return RedirectToAction(nameof(Index));
    }

    [HttpGet("subcategories/new")]
    public async Task<IActionResult> NewSubCategory(int? categoryId)
    {
        await LoadParentsAsync();
        return View("SubCategoryForm", new SubCategoryInput { ParentCategoryId = categoryId ?? 0 });
    }

    [HttpGet("subcategories/{id:int}/edit")]
    public async Task<IActionResult> EditSubCategory(int id)
    {
        var sub = await db.SubCategories.FindAsync(id);
        if (sub is null) return NotFound();
        await LoadParentsAsync();
        return View("SubCategoryForm", new SubCategoryInput
        {
            Id = sub.Id, ParentCategoryId = sub.ParentCategoryId, Name = sub.Name,
            Description = sub.Description, SortOrder = sub.SortOrder, IsActive = sub.IsActive
        });
    }

    [HttpPost("subcategories/save"), ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveSubCategory(SubCategoryInput input)
    {
        if (input.Id != 0 && !await db.SubCategories.AnyAsync(x => x.Id == input.Id)) return NotFound();
        var name = input.Name?.Trim() ?? "";
        var parent = await db.Categories.FindAsync(input.ParentCategoryId);
        if (parent is null) ModelState.AddModelError(nameof(input.ParentCategoryId), "اختر تصنيفاً رئيسياً موجوداً.");
        if (await db.SubCategories.AnyAsync(x => x.ParentCategoryId == input.ParentCategoryId
                                                && x.Name == name && x.Id != input.Id))
            ModelState.AddModelError(nameof(input.Name), "التصنيف الفرعي موجود مسبقاً تحت هذا التصنيف.");
        if (!ModelState.IsValid)
        {
            await LoadParentsAsync();
            return View("SubCategoryForm", input);
        }

        ProductSubCategory sub;
        if (input.Id == 0)
        {
            sub = new ProductSubCategory();
            db.SubCategories.Add(sub);
        }
        else
        {
            sub = await db.SubCategories.FindAsync(input.Id) ?? throw new InvalidOperationException("Subcategory not found.");
        }
        sub.ParentCategoryId = parent!.Id;
        sub.Name = name;
        sub.Description = input.Description?.Trim() ?? "";
        sub.SortOrder = input.SortOrder;
        sub.IsActive = input.IsActive;
        await db.SaveChangesAsync();
        await db.Products.Where(x => x.SubCategoryId == sub.Id)
            .ExecuteUpdateAsync(x => x.SetProperty(p => p.Category, parent.Name));
        TempData["Success"] = "تم حفظ التصنيف الفرعي.";
        return RedirectToAction(nameof(Index));
    }

    private async Task LoadParentsAsync() => ViewBag.Parents = await db.Categories.AsNoTracking()
        .OrderBy(x => x.SortOrder).ThenBy(x => x.Name).ToListAsync();
}
