using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using System.ComponentModel.DataAnnotations;
using TemporaryLinks.Addon.Services;

namespace TemporaryLinks.Addon.Pages.Links;

public class CreateModel : PageModel
{
    private readonly ILinkService _linkService;

    public CreateModel(ILinkService linkService)
    {
        _linkService = linkService;
    }

    [BindProperty]
    public CreateLinkInput Input { get; set; } = new();

    public class CreateLinkInput
    {
        [Required]
        [StringLength(256)]
        public string Name { get; set; } = string.Empty;

        [Required]
        [Display(Name = "Actions (JSON)")]
        public string Actions { get; set; } = string.Empty;

        [Required]
        [Display(Name = "Valid From")]
        public DateTime ValidFrom { get; set; } = DateTime.Now;

        [Required]
        [Display(Name = "Valid Until")]
        public DateTime ValidUntil { get; set; } = DateTime.Now.AddHours(8);

        [Phone]
        [Display(Name = "Recipient Phone Number")]
        public string? RecipientPhoneNumber { get; set; }

        [Display(Name = "Custom Message")]
        public string? CustomMessage { get; set; }

        [Display(Name = "Send SMS Immediately")]
        public bool SendSmsImmediately { get; set; } = true;

        [Required]
        [Range(1, 100)]
        [Display(Name = "Maximum Uses")]
        public int MaxUses { get; set; } = 1;
    }

    public void OnGet()
    {
        var today = DateTime.Today;
        Input.ValidFrom = today.AddHours(9);
        Input.ValidUntil = today.AddHours(17);
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!ModelState.IsValid)
        {
            return Page();
        }

        var baseUrl = GetBaseUrl();
        var link = await _linkService.CreateLinkAsync(
            name: Input.Name,
            scriptEntityId: "script.dummy",  // Dummy value - Actions will override this
            validFrom: new DateTimeOffset(Input.ValidFrom),
            validUntil: new DateTimeOffset(Input.ValidUntil),
            recipientPhoneNumber: Input.RecipientPhoneNumber,
            customMessage: Input.CustomMessage,
            scriptData: null,
            createdBy: "WebUI",
            baseUrl: baseUrl,
            sendSmsImmediately: Input.SendSmsImmediately,
            maxUses: Input.MaxUses,
            actions: Input.Actions);

        return RedirectToPage("Details", new { id = link.Id });
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
