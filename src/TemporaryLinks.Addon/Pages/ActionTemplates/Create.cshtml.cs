using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using System.ComponentModel.DataAnnotations;
using TemporaryLinks.Addon.Data;
using TemporaryLinks.Addon.Models;

namespace TemporaryLinks.Addon.Pages.ActionTemplates;

public class CreateModel : PageModel
{
    private readonly ApplicationDbContext _context;

    public CreateModel(ApplicationDbContext context)
    {
        _context = context;
    }

    [BindProperty]
    public CreateTemplateInput Input { get; set; } = new();

    public class CreateTemplateInput
    {
        [Required]
        [StringLength(256)]
        public string Name { get; set; } = string.Empty;

        [Required]
        [Display(Name = "Actions (JSON)")]
        public string Actions { get; set; } = string.Empty;

        [StringLength(1000)]
        public string? Description { get; set; }
    }

    public void OnGet()
    {
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!ModelState.IsValid)
        {
            return Page();
        }

        var template = new ActionTemplate
        {
            Name = Input.Name,
            Actions = Input.Actions,
            Description = Input.Description
        };

        _context.ActionTemplates.Add(template);
        await _context.SaveChangesAsync();

        return RedirectToPage("Index");
    }
}
