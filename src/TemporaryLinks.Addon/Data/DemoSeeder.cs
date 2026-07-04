using TemporaryLinks.Addon.Models;

namespace TemporaryLinks.Addon.Data;

/// <summary>
/// Dev-only sample data for local UI work. Guarded by the SEED_DEMO env var so it never
/// runs inside the add-on. Writes rows directly (no Home Assistant calls).
/// </summary>
public static class DemoSeeder
{
    public static void Seed(ApplicationDbContext db)
    {
        if (db.TemporaryLinks.Any())
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;

        db.Contacts.AddRange(
            new Contact { Name = "Erik the dog walker", PhoneNumber = "+46701112233", Email = "erik@example.com", Info = "Weekday afternoons", CreatedAt = now.AddDays(-40) },
            new Contact { Name = "Cleaning crew", PhoneNumber = "+46702223344", Info = "Every other Friday", CreatedAt = now.AddDays(-30) },
            new Contact { Name = "Grandma", PhoneNumber = "+46703334455", CreatedAt = now.AddDays(-12) });

        db.ActionTemplates.AddRange(
            new ActionTemplate { Name = "Unlock front door", Actions = "[{\"action\":\"lock.unlock\",\"target\":{\"entity_id\":\"lock.front_door\"}}]", Description = "Opens the main entrance lock", CreatedAt = now.AddDays(-40) },
            new ActionTemplate { Name = "Open the gate", Actions = "[{\"action\":\"cover.open_cover\",\"target\":{\"entity_id\":\"cover.driveway_gate\"}}]", Description = "Driveway gate", CreatedAt = now.AddDays(-25) },
            new ActionTemplate { Name = "Welcome scene", Actions = "[{\"action\":\"scene.turn_on\",\"target\":{\"entity_id\":\"scene.welcome\"}}]", CreatedAt = now.AddDays(-10) });

        var links = new[]
        {
            NewLink("Dog walker — Tuesday", "+46701112233", LinkStatus.Active, 0, 1, now.AddHours(-2), now.AddHours(6),
                "[{\"action\":\"lock.unlock\",\"target\":{\"entity_id\":\"lock.front_door\"}}]", now.AddHours(-3), "Erik"),
            NewLink("Cleaning crew access", "+46702223344", LinkStatus.Active, 1, 5, now.AddDays(-1), now.AddDays(6),
                "[{\"action\":\"cover.open_cover\",\"target\":{\"entity_id\":\"cover.driveway_gate\"}}]", now.AddDays(-1), "Anna"),
            NewLink("Package delivery", "+46704445566", LinkStatus.Used, 1, 1, now.AddDays(-3), now.AddDays(-2),
                "[{\"action\":\"lock.unlock\",\"target\":{\"entity_id\":\"lock.front_door\"}}]", now.AddDays(-3), "Anna"),
            NewLink("Weekend guest", "+46705556677", LinkStatus.Expired, 2, 4, now.AddDays(-9), now.AddDays(-6),
                "[{\"action\":\"scene.turn_on\",\"target\":{\"entity_id\":\"scene.welcome\"}}]", now.AddDays(-10), "Erik"),
            NewLink("Old contractor link", "+46706667788", LinkStatus.Revoked, 0, 1, now.AddDays(-14), now.AddDays(-1),
                "[{\"action\":\"cover.open_cover\",\"target\":{\"entity_id\":\"cover.driveway_gate\"}}]", now.AddDays(-15), "Anna"),
            NewLink("Grandma — standing button", null, LinkStatus.Active, 12, 999, now.AddDays(-12), now.AddDays(60),
                "[{\"action\":\"lock.unlock\",\"target\":{\"entity_id\":\"lock.front_door\"}}]", now.AddDays(-12), "Erik"),
        };
        db.TemporaryLinks.AddRange(links);
        db.SaveChanges();

        var audits = new List<LinkUsageAudit>
        {
            Audit(links[0].Id, "Created", "Link created by Erik (max uses: 1)", true, now.AddHours(-3)),
            Audit(links[1].Id, "Created", "Link created by Anna (max uses: 5)", true, now.AddDays(-1)),
            Audit(links[1].Id, "SmsSent", "SMS sent to +46702223344", true, now.AddDays(-1).AddMinutes(2)),
            Audit(links[1].Id, "Executed", "Link executed (1/5)", true, now.AddHours(-20)),
            Audit(links[2].Id, "Executed", "Link executed (1/1)", true, now.AddDays(-2).AddHours(3)),
            Audit(links[2].Id, "WebhookDeleted", "Webhook automation deleted (max uses reached)", true, now.AddDays(-2).AddHours(3)),
            Audit(links[3].Id, "ExecutionAttempt", "Attempted to use expired link", false, now.AddDays(-5)),
            Audit(links[3].Id, "Expired", "Link validity period ended", true, now.AddDays(-6)),
            Audit(links[4].Id, "Revoked", "Link was revoked", true, now.AddDays(-1)),
            Audit(links[5].Id, "Executed", "Link executed (12/999)", true, now.AddHours(-8)),
        };
        db.LinkUsageAudits.AddRange(audits);

        db.LinkSmsAudits.Add(new LinkSmsAudit
        {
            TemporaryLinkId = links[1].Id,
            Content = "Your temporary access link: https://hooks.nabu.casa/abc123\nValid from 07/02 to 07/08",
            TwilioMessageSid = "SM8f3a2b1c9d4e5f60",
            SmsSent = true,
            Timestamp = now.AddDays(-1).AddMinutes(2),
        });

        db.SaveChanges();
    }

    private static TemporaryLink NewLink(string name, string? phone, LinkStatus status, int used, int max,
        DateTimeOffset from, DateTimeOffset until, string actions, DateTimeOffset created, string by)
    {
        var token = Guid.NewGuid().ToString("N")[..24];
        return new TemporaryLink
        {
            Token = token,
            Name = name,
            Actions = actions,
            ValidFrom = from,
            ValidUntil = until,
            MaxUses = max,
            UsageCount = used,
            RecipientPhoneNumber = phone,
            Status = status,
            CreatedAt = created,
            CreatedBy = by,
            WebhookId = $"temp_link_{token}",
            CloudhookId = $"cloud_{token}",
            CloudhookUrl = $"https://hooks.nabu.casa/{token}",
        };
    }

    private static LinkUsageAudit Audit(Guid linkId, string type, string desc, bool ok, DateTimeOffset when) => new()
    {
        TemporaryLinkId = linkId,
        EventType = type,
        Description = desc,
        Success = ok,
        Timestamp = when,
    };
}
