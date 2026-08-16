using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using System.ComponentModel.DataAnnotations;
using TemporaryLinks.Addon.Data;
using TemporaryLinks.Addon.Models;
using TemporaryLinks.Addon.Services;

namespace TemporaryLinks.Addon.Pages.ActionTemplates;

public class EditModel : PageModel, IActionPickerSource
{
    private readonly ApplicationDbContext _context;
    private readonly IHomeAssistantService _haService;
    private readonly ILogger<EditModel> _logger;

    public EditModel(
        ApplicationDbContext context,
        IHomeAssistantService haService,
        ILogger<EditModel> logger)
    {
        _context = context;
        _haService = haService;
        _logger = logger;
    }

    [BindProperty]
    public EditTemplateInput Input { get; set; } = new();

    public ActionTemplate? Template { get; set; }

    public bool PickerAvailable { get; set; }
    public string ServicesJson { get; set; } = "[]";
    public string EntitiesJson { get; set; } = "[]";

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

        await ActionPickerRegistryLoader.LoadAsync(this, _haService, _logger);

        return Page();
    }

    public async Task<IActionResult> OnPostAsync(Guid id)
    {
        if (!ModelState.IsValid)
        {
            Template = await _context.ActionTemplates.FindAsync(id);
            await ActionPickerRegistryLoader.LoadAsync(this, _haService, _logger);
            return Page();
        }

        Template = await _context.ActionTemplates.FindAsync(id);

        if (Template == null)
        {
            return NotFound();
        }

        string normalizedActions;
        try
        {
            // Same normalization/validation link creation enforces: a saved template can
            // never later be refused by link creation on the form of its actions.
            normalizedActions = ActionsNormalizer.Normalize(Input.Actions);
        }
        catch (InvalidOperationException ex)
        {
            ModelState.AddModelError("Input.Actions", ex.Message);
            await ActionPickerRegistryLoader.LoadAsync(this, _haService, _logger);
            return Page();
        }

        Template.Name = Input.Name;
        Template.Actions = normalizedActions;
        Template.Description = Input.Description;
        Template.UpdatedAt = DateTimeOffset.UtcNow;

        await _context.SaveChangesAsync();

        return RedirectToPage("Index");
    }
}
