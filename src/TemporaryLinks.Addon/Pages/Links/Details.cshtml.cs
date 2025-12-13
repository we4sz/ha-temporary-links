using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TemporaryLinks.Addon.Models;
using TemporaryLinks.Addon.Services;

namespace TemporaryLinks.Addon.Pages.Links;

public class DetailsModel : PageModel
{
    private readonly ILinkService _linkService;
    private readonly ITwilioService _twilioService;

    public DetailsModel(ILinkService linkService, ITwilioService twilioService)
    {
        _linkService = linkService;
        _twilioService = twilioService;
    }

    public TemporaryLink? Link { get; set; }
    public string LinkUrl { get; set; } = string.Empty;
    public bool IsTwilioConfigured { get; set; }

    [TempData]
    public string? ErrorMessage { get; set; }

    [TempData]
    public string? SuccessMessage { get; set; }

    public async Task<IActionResult> OnGetAsync(Guid id)
    {
        Link = await _linkService.GetLinkByIdAsync(id);
        LinkUrl = Link?.CloudhookUrl ?? "";
        IsTwilioConfigured = _twilioService.IsConfigured;

        return Page();
    }

    public async Task<IActionResult> OnPostSendSmsAsync(Guid id)
    {
        if (!_twilioService.IsConfigured)
        {
            ErrorMessage = "Twilio is not configured. Please configure Twilio settings to send SMS.";
            return RedirectToPage(new { id });
        }

        Link = await _linkService.GetLinkByIdAsync(id);

        if (Link == null)
        {
            ErrorMessage = "Link not found.";
            return RedirectToPage(new { id });
        }

        try
        {
            await _linkService.SendSmsAsync(Link);
            SuccessMessage = $"SMS sent successfully to {Link.RecipientPhoneNumber}";
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Failed to send SMS: {ex.Message}";
        }

        return RedirectToPage(new { id });
    }
}
