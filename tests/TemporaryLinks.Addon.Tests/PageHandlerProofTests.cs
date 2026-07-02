using System.ComponentModel.DataAnnotations;
using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using TemporaryLinks.Addon.Models;
using Xunit;
using ContactsCreate = TemporaryLinks.Addon.Pages.Contacts.CreateModel;
using ContactsEdit = TemporaryLinks.Addon.Pages.Contacts.EditModel;
using ContactsDelete = TemporaryLinks.Addon.Pages.Contacts.DeleteModel;
using TemplatesCreate = TemporaryLinks.Addon.Pages.ActionTemplates.CreateModel;
using LinksCreate = TemporaryLinks.Addon.Pages.Links.CreateModel;
using Dashboard = TemporaryLinks.Addon.Pages.IndexModel;

namespace TemporaryLinks.Addon.Tests;

/// <summary>Razor page-handler proofs (constructed directly, no web host) for E4 and E5.S1.</summary>
public class PageHandlerProofTests
{
    private static LinksCreate NewLinksCreate(LinkServiceHarness h) =>
        new(h.Service, h.Db, h.Ha, NullLogger<LinksCreate>.Instance);

    // Proves app::E4.S1.A1 — contacts create/edit/delete persist; a contact requires a name
    // and a phone number; edits record when they happened.
    [Fact]
    public async Task Contacts_crud_persists_and_edits_are_timestamped()
    {
        using var h = new LinkServiceHarness();

        var create = new ContactsCreate(h.Db)
        {
            Input = new ContactsCreate.CreateContactInput { Name = "Dog walker", PhoneNumber = "+15551110000" },
        };
        await create.OnPostAsync();
        var contact = Assert.Single(h.Db.Contacts);
        Assert.Null(contact.UpdatedAt);

        var edit = new ContactsEdit(h.Db)
        {
            Input = new ContactsEdit.EditContactInput { Name = "Dog walker 2", PhoneNumber = "+15551110001" },
        };
        await edit.OnPostAsync(contact.Id);
        await h.Db.Entry(contact).ReloadAsync();
        Assert.Equal("Dog walker 2", contact.Name);
        Assert.NotNull(contact.UpdatedAt);

        var del = new ContactsDelete(h.Db);
        await del.OnPostAsync(contact.Id);
        Assert.Empty(h.Db.Contacts);

        // Name and phone are required.
        Assert.NotNull(typeof(ContactsCreate.CreateContactInput).GetProperty("Name")!
            .GetCustomAttribute<RequiredAttribute>());
        Assert.NotNull(typeof(ContactsCreate.CreateContactInput).GetProperty("PhoneNumber")!
            .GetCustomAttribute<RequiredAttribute>());
    }

    // Proves app::E4.S2.A1 — action templates persist; a template requires a name and actions.
    [Fact]
    public async Task Templates_create_persists()
    {
        using var h = new LinkServiceHarness();
        var create = new TemplatesCreate(h.Db)
        {
            Input = new TemplatesCreate.CreateTemplateInput
            {
                Name = "Unlock front door",
                Actions = "[{\"action\":\"lock.unlock\"}]",
            },
        };

        await create.OnPostAsync();

        var t = Assert.Single(h.Db.ActionTemplates);
        Assert.Equal("Unlock front door", t.Name);
        Assert.NotNull(typeof(TemplatesCreate.CreateTemplateInput).GetProperty("Name")!
            .GetCustomAttribute<RequiredAttribute>());
        Assert.NotNull(typeof(TemplatesCreate.CreateTemplateInput).GetProperty("Actions")!
            .GetCustomAttribute<RequiredAttribute>());
    }

    // Proves app::E4.S1.A2 / E4.S2.A2 — the create form offers saved contacts and templates
    // as pickable alternatives to typing.
    [Fact]
    public async Task Create_form_offers_saved_contacts_and_templates()
    {
        using var h = new LinkServiceHarness();
        h.Db.Contacts.Add(new Contact { Name = "Cleaner", PhoneNumber = "+15552220000" });
        h.Db.ActionTemplates.Add(new ActionTemplate { Name = "Gate", Actions = "[]" });
        await h.Db.SaveChangesAsync();

        var page = NewLinksCreate(h);
        await page.OnGetAsync();

        Assert.Contains(page.ContactOptions, o => o.Value == "+15552220000");
        Assert.Contains(page.TemplateOptions, o => o.Text == "Gate");
    }

    // Proves app::E4.S3.A1 — opting to save actions as a template at creation adds one,
    // unless an identical set is already saved.
    [Fact]
    public async Task Save_as_template_at_creation_dedupes_by_actions()
    {
        using var h = new LinkServiceHarness();
        var now = DateTime.Now;

        async Task CreateWithSave(string actions)
        {
            var page = NewLinksCreate(h);
            page.Input = new LinksCreate.CreateLinkInput
            {
                Name = "L", Actions = actions, ValidFrom = now, ValidUntil = now.AddHours(1),
                RecipientPhoneNumber = "+15553330000", MaxUses = 1,
            };
            page.SaveAsTemplate = true;
            await page.OnPostAsync();
        }

        await CreateWithSave("[{\"action\":\"lock.unlock\"}]");
        await CreateWithSave("[{\"action\":\"lock.unlock\"}]"); // identical → no duplicate

        Assert.Single(h.Db.ActionTemplates);
    }

    // Proves app::E4.S3.A2 — opting to save the recipient as a contact dedupes by number.
    [Fact]
    public async Task Save_as_contact_at_creation_dedupes_by_number()
    {
        using var h = new LinkServiceHarness();
        var now = DateTime.Now;

        async Task CreateWithSave()
        {
            var page = NewLinksCreate(h);
            page.Input = new LinksCreate.CreateLinkInput
            {
                Name = "L", Actions = "[]", ValidFrom = now, ValidUntil = now.AddHours(1),
                RecipientPhoneNumber = "+15554440000", MaxUses = 1,
            };
            page.SaveAsContact = true;
            await page.OnPostAsync();
        }

        await CreateWithSave();
        await CreateWithSave(); // same number → no duplicate

        Assert.Single(h.Db.Contacts);
    }

    // Proves app::E4.S4.A1 — duplicating prefills name/actions/recipient/message/allowance but
    // a fresh window (not copied).
    [Fact]
    public async Task Duplicate_prefills_details_but_not_the_window()
    {
        using var h = new LinkServiceHarness();
        var source = await h.SeedLinkAsync(maxUses: 4, customMessage: "hi");
        source.Name = "Original";
        source.Actions = "[{\"action\":\"scene.turn_on\"}]";
        await h.Db.SaveChangesAsync();

        var page = NewLinksCreate(h);
        await page.OnGetAsync(source.Id);

        Assert.Equal("Original", page.Input.Name);
        Assert.Equal(source.Actions, page.Input.Actions);
        Assert.Equal(source.RecipientPhoneNumber, page.Input.RecipientPhoneNumber);
        Assert.Equal(4, page.Input.MaxUses);
        Assert.NotEqual(source.ValidFrom.DateTime, page.Input.ValidFrom); // window is fresh
    }

    // Proves app::E5.S1.A1 — the overview shows per-status counts, active links, and recent
    // activity.
    [Fact]
    public async Task Dashboard_shows_counts_active_links_and_recent_activity()
    {
        using var h = new LinkServiceHarness();
        await h.SeedLinkAsync(status: LinkStatus.Active);
        await h.SeedLinkAsync(status: LinkStatus.Used);
        var expired = await h.SeedLinkAsync(status: LinkStatus.Expired);
        h.Db.LinkUsageAudits.Add(new LinkUsageAudit
        {
            TemporaryLinkId = expired.Id, EventType = "Expired", Description = "x",
        });
        await h.Db.SaveChangesAsync();

        var page = new Dashboard(h.Db);
        await page.OnGetAsync();

        Assert.Equal(3, page.TotalLinks);
        Assert.Equal(1, page.ActiveLinks);
        Assert.Equal(1, page.UsedLinks);
        Assert.Equal(1, page.ExpiredLinks);
        Assert.Single(page.ActiveLinksList);
        Assert.NotEmpty(page.RecentActivity);
    }
}
