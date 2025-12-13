using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;
using TemporaryLinks.Addon.Data;
using TemporaryLinks.Addon.Models;

namespace TemporaryLinks.Addon.Pages.Contacts;

public class EditModel : PageModel
{
    private readonly ApplicationDbContext _context;

    public EditModel(ApplicationDbContext context)
    {
        _context = context;
    }

    [BindProperty]
    public EditContactInput Input { get; set; } = new();

    public Contact? Contact { get; set; }

    public class EditContactInput
    {
        [Required]
        [StringLength(256)]
        public string Name { get; set; } = string.Empty;

        [Required]
        [Phone]
        [Display(Name = "Phone Number")]
        public string PhoneNumber { get; set; } = string.Empty;

        [EmailAddress]
        [StringLength(256)]
        public string? Email { get; set; }

        [StringLength(1000)]
        public string? Info { get; set; }
    }

    public async Task<IActionResult> OnGetAsync(Guid id)
    {
        Contact = await _context.Contacts.FindAsync(id);

        if (Contact == null)
        {
            return Page();
        }

        Input = new EditContactInput
        {
            Name = Contact.Name,
            PhoneNumber = Contact.PhoneNumber,
            Email = Contact.Email,
            Info = Contact.Info
        };

        return Page();
    }

    public async Task<IActionResult> OnPostAsync(Guid id)
    {
        if (!ModelState.IsValid)
        {
            Contact = await _context.Contacts.FindAsync(id);
            return Page();
        }

        Contact = await _context.Contacts.FindAsync(id);

        if (Contact == null)
        {
            return NotFound();
        }

        Contact.Name = Input.Name;
        Contact.PhoneNumber = Input.PhoneNumber;
        Contact.Email = Input.Email;
        Contact.Info = Input.Info;
        Contact.UpdatedAt = DateTimeOffset.UtcNow;

        await _context.SaveChangesAsync();

        return RedirectToPage("Index");
    }
}
