using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using TemporaryLinks.Addon.Models;
using Xunit;
using TemplatesCreate = TemporaryLinks.Addon.Pages.ActionTemplates.CreateModel;
using TemplatesEdit = TemporaryLinks.Addon.Pages.ActionTemplates.EditModel;

namespace TemporaryLinks.Addon.Tests;

/// <summary>Proves app::E4.S2.A4 — a template's actions are validated and normalized to
/// exactly the contract link creation enforces at save time (create and edit alike), so a
/// saved template can never later be refused by link creation on the form of its actions.</summary>
public class TemplateContractProofTests
{
    private static TemplatesCreate NewCreate(LinkServiceHarness h) =>
        new(h.Db, h.Ha, NullLogger<TemplatesCreate>.Instance);

    private static TemplatesEdit NewEdit(LinkServiceHarness h) =>
        new(h.Db, h.Ha, NullLogger<TemplatesEdit>.Instance);

    // Home Assistant's own automation syntax ("service") is accepted at template save and
    // normalized to the contract, exactly as link creation does.
    [Fact]
    public async Task Create_normalizes_service_keyed_actions_to_the_execution_contract()
    {
        using var h = new LinkServiceHarness();
        var create = NewCreate(h);
        create.Input = new TemplatesCreate.CreateTemplateInput
        {
            Name = "Unlock front door",
            Actions = """[{"service":"lock.unlock","target":{"entity_id":"lock.front_door"}}]""",
        };

        await create.OnPostAsync();

        var t = Assert.Single(h.Db.ActionTemplates);
        using var doc = JsonDocument.Parse(t.Actions);
        var action = doc.RootElement[0];
        Assert.Equal("lock.unlock", action.GetProperty("action").GetString());
        Assert.False(action.TryGetProperty("service", out _));
        Assert.Equal("lock.front_door",
            action.GetProperty("target").GetProperty("entity_id").GetString());
    }

    // A top-level entity_id (again the home's automation syntax) becomes a target at save
    // time, so execution finds it where it looks.
    [Fact]
    public async Task Create_normalizes_top_level_entity_id_to_a_target()
    {
        using var h = new LinkServiceHarness();
        var create = NewCreate(h);
        create.Input = new TemplatesCreate.CreateTemplateInput
        {
            Name = "Hall light",
            Actions = """[{"action":"light.turn_on","entity_id":"light.hall"}]""",
        };

        await create.OnPostAsync();

        var t = Assert.Single(h.Db.ActionTemplates);
        using var doc = JsonDocument.Parse(t.Actions);
        var action = doc.RootElement[0];
        Assert.Equal("light.hall",
            action.GetProperty("target").GetProperty("entity_id").GetString());
        Assert.False(action.TryGetProperty("entity_id", out _));
    }

    // A form link creation would refuse (here: HA's device-action shape, which has neither
    // "action" nor "service") is refused at template save too, with a clear explanation, and
    // nothing is persisted.
    [Fact]
    public async Task Create_refuses_a_device_action_form_and_saves_nothing()
    {
        using var h = new LinkServiceHarness();
        var create = NewCreate(h);
        create.Input = new TemplatesCreate.CreateTemplateInput
        {
            Name = "Legacy 1.0.x template",
            Actions = """[{"device_id":"abc123","domain":"lock","type":"lock"}]""",
        };

        await create.OnPostAsync();

        Assert.False(create.ModelState.IsValid);
        var entry = create.ModelState["Input.Actions"];
        Assert.NotNull(entry);
        var error = Assert.Single(entry!.Errors);
        Assert.False(string.IsNullOrWhiteSpace(error.ErrorMessage));
        Assert.Empty(h.Db.ActionTemplates);
    }

    // The same normalization applies on Edit: a legacy or hand-edited draft is rewritten to
    // the canonical contract on save.
    [Fact]
    public async Task Edit_normalizes_actions_to_the_execution_contract_on_save()
    {
        using var h = new LinkServiceHarness();
        var template = new ActionTemplate { Name = "Gate", Actions = "[]" };
        h.Db.ActionTemplates.Add(template);
        await h.Db.SaveChangesAsync();

        var edit = NewEdit(h);
        await edit.OnGetAsync(template.Id);
        edit.Input.Actions = """[{"service":"cover.open_cover","entity_id":"cover.gate"}]""";

        await edit.OnPostAsync(template.Id);

        await h.Db.Entry(template).ReloadAsync();
        using var doc = JsonDocument.Parse(template.Actions);
        var action = doc.RootElement[0];
        Assert.Equal("cover.open_cover", action.GetProperty("action").GetString());
        Assert.Equal("cover.gate",
            action.GetProperty("target").GetProperty("entity_id").GetString());
    }

    // The same refusal applies on Edit: a form link creation would refuse is refused at save,
    // with a clear explanation, and the stored template is left untouched.
    [Fact]
    public async Task Edit_refuses_a_device_action_form_and_leaves_the_template_unchanged()
    {
        using var h = new LinkServiceHarness();
        var template = new ActionTemplate
        {
            Name = "Gate",
            Actions = """[{"action":"cover.open_cover"}]""",
        };
        h.Db.ActionTemplates.Add(template);
        await h.Db.SaveChangesAsync();

        var edit = NewEdit(h);
        await edit.OnGetAsync(template.Id);
        edit.Input.Actions = """[{"device_id":"abc123","domain":"lock","type":"lock"}]""";

        await edit.OnPostAsync(template.Id);

        Assert.False(edit.ModelState.IsValid);
        var entry = edit.ModelState["Input.Actions"];
        Assert.NotNull(entry);
        var error = Assert.Single(entry!.Errors);
        Assert.False(string.IsNullOrWhiteSpace(error.ErrorMessage));

        await h.Db.Entry(template).ReloadAsync();
        Assert.Equal("""[{"action":"cover.open_cover"}]""", template.Actions);
    }
}
