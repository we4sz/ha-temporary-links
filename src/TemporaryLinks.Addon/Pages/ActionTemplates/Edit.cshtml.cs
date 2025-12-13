using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using System.ComponentModel.DataAnnotations;
using TemporaryLinks.Addon.Data;
using TemporaryLinks.Addon.Models;

namespace TemporaryLinks.Addon.Pages.ActionTemplates;

public class EditModel : PageModel
{
    private readonly ApplicationDbContext _context;

    public EditModel(ApplicationDbContext context)
    {
        _context = context;
    }

    [BindProperty]
    public EditTemplateInput Input { get; set; } = new();

    public ActionTemplate? Template { get; set; }

    public class EditTemplateInput
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

    public async Task<IActionResult> OnGetAsync(Guid id)
    {
        Template = await _context.ActionTemplates.FindAsync(id);

        if (Template == null)
        {
            return Page();
        }

        Input = new EditTemplateInput
        {
            Name = Template.Name,
            Actions = Template.Actions,
            Description = Template.Description
        };

        return Page();
    }

    public async Task<IActionResult> OnPostAsync(Guid id)
    {
        if (!ModelState.IsValid)
        {
            Template = await _context.ActionTemplates.FindAsync(id);
            return Page();
        }

        Template = await _context.ActionTemplates.FindAsync(id);

        if (Template == null)
        {
            return NotFound();
        }

        Template.Name = Input.Name;
        Template.Actions = Input.Actions;
        Template.Description = Input.Description;
        Template.UpdatedAt = DateTimeOffset.UtcNow;

        await _context.SaveChangesAsync();

        return RedirectToPage("Index");
    }
}
