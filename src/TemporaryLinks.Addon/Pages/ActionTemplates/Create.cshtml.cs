using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using System.ComponentModel.DataAnnotations;
using TemporaryLinks.Addon.Data;
using TemporaryLinks.Addon.Models;
using TemporaryLinks.Addon.Services;

namespace TemporaryLinks.Addon.Pages.ActionTemplates;

public class CreateModel : PageModel, IActionPickerSource
{
    private readonly ApplicationDbContext _context;
    private readonly IHomeAssistantService _haService;
    private readonly ILogger<CreateModel> _logger;

    public CreateModel(
        ApplicationDbContext context,
        IHomeAssistantService haService,
        ILogger<CreateModel> logger)
    {
        _context = context;
        _haService = haService;
        _logger = logger;
    }

    [BindProperty]
    public CreateTemplateInput Input { get; set; } = new();

    public bool PickerAvailable { get; set; }
    public string ServicesJson { get; set; } = "[]";
    public string EntitiesJson { get; set; } = "[]";

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

    public async Task OnGetAsync()
    {
        await ActionPickerRegistryLoader.LoadAsync(this, _haService, _logger);
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!ModelState.IsValid)
        {
            await ActionPickerRegistryLoader.LoadAsync(this, _haService, _logger);
            return Page();
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

        var template = new ActionTemplate
        {
            Name = Input.Name,
            Actions = normalizedActions,
            Description = Input.Description
        };

        _context.ActionTemplates.Add(template);
        await _context.SaveChangesAsync();

        return RedirectToPage("Index");
    }
}
