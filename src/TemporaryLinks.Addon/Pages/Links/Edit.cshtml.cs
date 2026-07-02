using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;
using TemporaryLinks.Addon.Data;
using TemporaryLinks.Addon.Models;
using TemporaryLinks.Addon.Services;

namespace TemporaryLinks.Addon.Pages.Links;

public class EditModel : PageModel
{
    private readonly ILinkService _linkService;
    private readonly ApplicationDbContext _context;

    public EditModel(ILinkService linkService, ApplicationDbContext context)
    {
        _linkService = linkService;
        _context = context;
    }

    [BindProperty]
    public EditLinkInput Input { get; set; } = new();

    public TemporaryLink? Link { get; set; }
    public List<SelectListItem> ContactOptions { get; set; } = new();

    public class EditLinkInput
    {
        [Required]
        [Display(Name = "Valid From")]
        public DateTime ValidFrom { get; set; }

        [Required]
        [Display(Name = "Valid Until")]
        public DateTime ValidUntil { get; set; }

        [Phone]
        [Display(Name = "Recipient Phone Number (optional)")]
        public string? RecipientPhoneNumber { get; set; }

        [Display(Name = "Custom Message")]
        public string? CustomMessage { get; set; }

        [Required]
        [Range(1, int.MaxValue)]
        [Display(Name = "Maximum Uses")]
        public int MaxUses { get; set; }
    }

    public async Task<IActionResult> OnGetAsync(Guid id)
    {
        Link = await _linkService.GetLinkByIdAsync(id);

        if (Link == null)
        {
            return Page();
        }

        Input = new EditLinkInput
        {
            ValidFrom = Link.ValidFrom.LocalDateTime,
            ValidUntil = Link.ValidUntil.LocalDateTime,
            RecipientPhoneNumber = Link.RecipientPhoneNumber,
            CustomMessage = Link.CustomMessage,
            MaxUses = Link.MaxUses
        };

        await LoadContactsAsync();

        return Page();
    }

    public async Task<IActionResult> OnPostAsync(Guid id)
    {
        Link = await _linkService.GetLinkByIdAsync(id);

        if (Link == null)
        {
            return NotFound();
        }

        if (!ModelState.IsValid)
        {
            await LoadContactsAsync();
            return Page();
        }

        try
        {
            await _linkService.UpdateLinkAsync(
                id: id,
                validFrom: new DateTimeOffset(Input.ValidFrom),
                validUntil: new DateTimeOffset(Input.ValidUntil),
                recipientPhoneNumber: Input.RecipientPhoneNumber,
                customMessage: Input.CustomMessage,
                maxUses: Input.MaxUses);

            return RedirectToPage("Details", new { id });
        }
        catch (Exception ex)
        {
            ModelState.AddModelError(string.Empty, $"Failed to update link: {ex.Message}");
            await LoadContactsAsync();
            return Page();
        }
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
}
