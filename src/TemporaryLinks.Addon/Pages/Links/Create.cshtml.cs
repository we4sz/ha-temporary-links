using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;
using TemporaryLinks.Addon.Data;
using TemporaryLinks.Addon.Models;
using TemporaryLinks.Addon.Services;

namespace TemporaryLinks.Addon.Pages.Links;

public class CreateModel : PageModel
{
    private readonly ILinkService _linkService;
    private readonly ApplicationDbContext _context;

    public CreateModel(ILinkService linkService, ApplicationDbContext context)
    {
        _linkService = linkService;
        _context = context;
    }

    [BindProperty]
    public CreateLinkInput Input { get; set; } = new();

    public List<SelectListItem> ContactOptions { get; set; } = new();
    public List<SelectListItem> TemplateOptions { get; set; } = new();

    [BindProperty]
    public bool SaveAsContact { get; set; }

    [BindProperty]
    public bool SaveAsTemplate { get; set; }

    public class CreateLinkInput
    {
        [Required]
        [StringLength(256)]
        public string Name { get; set; } = string.Empty;

        [Required]
        [Display(Name = "Actions (JSON)")]
        public string Actions { get; set; }= string.Empty;

        [Required]
        [Display(Name = "Valid From")]
        public DateTime ValidFrom { get; set; } = DateTime.Now;

        [Required]
        [Display(Name = "Valid Until")]
        public DateTime ValidUntil { get; set; } = DateTime.Now.AddHours(24);

        [Phone]
        [Required]
        [Display(Name = "Recipient Phone Number")]
        public string RecipientPhoneNumber { get; set; }= string.Empty;

        [Display(Name = "Custom Message")]
        public string? CustomMessage { get; set; }

        [Required]
        [Range(1, int.MaxValue)]
        [Display(Name = "Maximum Uses")]
        public int MaxUses { get; set; } = 1;
    }

    public async Task OnGetAsync(Guid? duplicateFrom = null)
    {
        var today = DateTime.Today;
        Input.ValidFrom = today.AddHours(9);
        Input.ValidUntil = today.AddHours(17);

        // If duplicating from an existing link, pre-fill the form
        if (duplicateFrom.HasValue)
        {
            var existingLink = await _context.TemporaryLinks.FindAsync(duplicateFrom.Value);
            if (existingLink != null)
            {
                Input.Name = existingLink.Name;
                Input.Actions = existingLink.Actions;
                Input.RecipientPhoneNumber = existingLink.RecipientPhoneNumber;
                Input.CustomMessage = existingLink.CustomMessage;
                Input.MaxUses = existingLink.MaxUses;
                // Keep the default times (today 9am-5pm) for new link
            }
        }

        await LoadContactsAsync();
        await LoadTemplatesAsync();
    }

    private async Task LoadContactsAsync()
    {
        var contacts = await _context.Contacts
            .OrderBy(c => c.Name)
            .ToListAsync();

        ContactOptions = new List<SelectListItem>
        {
            new SelectListItem { Value = "", Text = "-- Select a contact or enter manually --" }
        };

        ContactOptions.AddRange(contacts.Select(c => new SelectListItem
        {
            Value = c.PhoneNumber,
            Text = $"{c.Name} ({c.PhoneNumber})"
        }));
    }

    private async Task LoadTemplatesAsync()
    {
        var templates = await _context.ActionTemplates
            .OrderBy(t => t.Name)
            .ToListAsync();

        TemplateOptions = new List<SelectListItem>
        {
            new SelectListItem { Value = "", Text = "-- Select a template or enter manually --" }
        };

        TemplateOptions.AddRange(templates.Select(t => new SelectListItem
        {
            Value = t.Actions,
            Text = t.Name
        }));
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!ModelState.IsValid)
        {
            await LoadContactsAsync();
            await LoadTemplatesAsync();
            return Page();
        }

        // Save as template if requested
        if (SaveAsTemplate && !string.IsNullOrWhiteSpace(Input.Actions))
        {
            // Check if template already exists
            var existingTemplate = await _context.ActionTemplates
                .FirstOrDefaultAsync(t => t.Actions == Input.Actions);

            if (existingTemplate == null)
            {
                var newTemplate = new ActionTemplate
                {
                    Name = Input.Name,
                    Actions = Input.Actions,
                    Description = $"Added from link: {Input.Name}"
                };
                _context.ActionTemplates.Add(newTemplate);
                await _context.SaveChangesAsync();
            }
        }

        // Save as contact if requested
        if (SaveAsContact && !string.IsNullOrWhiteSpace(Input.RecipientPhoneNumber))
        {
            // Check if contact already exists
            var existingContact = await _context.Contacts
                .FirstOrDefaultAsync(c => c.PhoneNumber == Input.RecipientPhoneNumber);

            if (existingContact == null)
            {
                var newContact = new Contact
                {
                    Name = Input.Name,
                    PhoneNumber = Input.RecipientPhoneNumber,
                    Info = $"Added from link: {Input.Name}"
                };
                _context.Contacts.Add(newContact);
                await _context.SaveChangesAsync();
            }
        }

        try
        {
            var link = await _linkService.CreateLinkAsync(
                name: Input.Name,
                validFrom: new DateTimeOffset(Input.ValidFrom),
                validUntil: new DateTimeOffset(Input.ValidUntil),
                recipientPhoneNumber: Input.RecipientPhoneNumber,
                customMessage: Input.CustomMessage,
                createdBy: "WebUI",
                maxUses: Input.MaxUses,
                actions: Input.Actions);

            return RedirectToPage("Details", new { id = link.Id });
        }
        catch (Exception ex)
        {
            ModelState.AddModelError(string.Empty, $"Failed to create link: {ex.Message}");
            await LoadContactsAsync();
            await LoadTemplatesAsync();
            return Page();
        }
    }

    private string GetBaseUrl()
    {
        var ingressPath = Request.Headers["X-Ingress-Path"].FirstOrDefault();
        if (!string.IsNullOrEmpty(ingressPath))
        {
            return ingressPath.TrimEnd('/');
        }
        return $"{Request.Scheme}://{Request.Host}";
    }
}
