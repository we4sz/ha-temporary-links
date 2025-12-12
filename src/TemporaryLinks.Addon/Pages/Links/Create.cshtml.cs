using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using System.ComponentModel.DataAnnotations;
using TemporaryLinks.Addon.Services;

namespace TemporaryLinks.Addon.Pages.Links;

public class CreateModel : PageModel
{
    private readonly ILinkService _linkService;
    private readonly IHomeAssistantService _haService;

    public CreateModel(ILinkService linkService, IHomeAssistantService haService)
    {
        _linkService = linkService;
        _haService = haService;
    }

    [BindProperty]
    public CreateLinkInput Input { get; set; } = new();

    public List<SelectListItem> ScriptEntities { get; set; } = [];

    public class CreateLinkInput
    {
        [Required]
        [StringLength(256)]
        public string Name { get; set; } = string.Empty;

        [Required]
        [StringLength(256)]
        [Display(Name = "Script Entity ID")]
        public string ScriptEntityId { get; set; } = string.Empty;

        [Display(Name = "Script Data (JSON)")]
        public string? ScriptData { get; set; }

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
    }

    public async Task OnGetAsync()
    {
        var today = DateTime.Today;
        Input.ValidFrom = today.AddHours(9);
        Input.ValidUntil = today.AddHours(17);

        await LoadScriptEntitiesAsync();
    }

    private async Task LoadScriptEntitiesAsync()
    {
        var entities = await _haService.GetEntitiesAsync("script");
        ScriptEntities = entities
            .Select(e => new SelectListItem(
                e.FriendlyName ?? e.EntityId,
                e.EntityId))
            .ToList();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!ModelState.IsValid)
        {
            await LoadScriptEntitiesAsync();
            return Page();
        }

        var baseUrl = GetBaseUrl();
        var link = await _linkService.CreateLinkAsync(
            name: Input.Name,
            scriptEntityId: Input.ScriptEntityId,
            validFrom: new DateTimeOffset(Input.ValidFrom),
            validUntil: new DateTimeOffset(Input.ValidUntil),
            recipientPhoneNumber: Input.RecipientPhoneNumber,
            customMessage: Input.CustomMessage,
            scriptData: Input.ScriptData,
            createdBy: "WebUI",
            baseUrl: baseUrl,
            sendSmsImmediately: Input.SendSmsImmediately);

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
