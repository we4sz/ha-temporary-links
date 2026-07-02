using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TemporaryLinks.Addon.Configuration;
using TemporaryLinks.Addon.Data;
using TemporaryLinks.Addon.Models;
using TemporaryLinks.Addon.Services;

namespace TemporaryLinks.Addon.Tests;

public sealed class FakeHomeAssistantService : IHomeAssistantService
{
    public List<string> CreatedAutomations { get; } = [];
    public List<(string AutomationId, DateTimeOffset ValidFrom, DateTimeOffset ValidUntil)> ArmedWindows { get; } = [];
    public List<string> DeletedAutomations { get; } = [];
    public Exception? ThrowOnDelete { get; set; }
    public Exception? ThrowOnCloudhook { get; set; }

    public Task<string> CreateWebhookAutomationAsync(
        string token, string linkName, string actionsJson,
        DateTimeOffset validFrom, DateTimeOffset validUntil,
        CancellationToken cancellationToken = default)
    {
        var id = $"temp_link_{token}";
        CreatedAutomations.Add(id);
        ArmedWindows.Add((id, validFrom, validUntil));
        return Task.FromResult(id);
    }

    public Task DeleteWebhookAutomationAsync(
        string automationId, CancellationToken cancellationToken = default)
    {
        if (ThrowOnDelete != null)
            throw ThrowOnDelete;
        DeletedAutomations.Add(automationId);
        return Task.CompletedTask;
    }

    public Task<CloudhookResult> CreateCloudhookAsync(
        string webhookId, CancellationToken cancellationToken = default)
    {
        if (ThrowOnCloudhook != null)
            throw ThrowOnCloudhook;
        return Task.FromResult(new CloudhookResult(
            webhookId, $"cloud_{webhookId}", $"https://hooks.nabu.casa/{webhookId}"));
    }

    public string? RemoteUiUrl { get; set; }

    public Task<string?> GetRemoteUiUrlAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(RemoteUiUrl);

    public Task<IReadOnlyList<HaServiceInfo>> GetServicesAsync(
        CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<HaServiceInfo>>(
            [new HaServiceInfo("light", "turn_on", "Turn on")]);

    public Task<IReadOnlyList<HaEntityInfo>> GetEntitiesAsync(
        CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<HaEntityInfo>>(
            [new HaEntityInfo("light.hall", "Hall light")]);
}

public sealed class FakeTwilioService : ITwilioService
{
    public List<(string To, string Message)> Sent { get; } = [];
    public bool FailNextSend { get; set; }

    public bool IsConfigured => true;

    public Task<TwilioSendResult> SendSmsAsync(
        string toPhoneNumber, string message, CancellationToken cancellationToken = default)
    {
        if (FailNextSend)
            return Task.FromResult(new TwilioSendResult { Success = false, ErrorMessage = "fake failure" });
        Sent.Add((toPhoneNumber, message));
        return Task.FromResult(new TwilioSendResult { Success = true, MessageSid = $"SM{Sent.Count:D4}" });
    }

    public Task<bool> ValidateConfigurationAsync() => Task.FromResult(true);
}

/// <summary>
/// One SQLite in-memory database + a LinkService wired with fakes, per test.
/// </summary>
public sealed class LinkServiceHarness : IDisposable
{
    private readonly SqliteConnection _connection;

    public ApplicationDbContext Db { get; }
    public FakeHomeAssistantService Ha { get; } = new();
    public FakeTwilioService Twilio { get; } = new();
    public LinkService Service { get; }

    public LinkServiceHarness(
        string defaultTemplate =
            "Your temporary access link: {link}\nValid from {start_time} to {end_time}",
        string? publicUrl = null,
        string? sharePageUrl = null)
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(_connection)
            .Options;
        Db = new ApplicationDbContext(options);
        Db.Database.EnsureCreated();

        Service = new LinkService(
            Db,
            new TokenGenerator(),
            Twilio,
            Ha,
            Options.Create(new AddonConfiguration
            {
                HaUrl = "http://ha.test:8123",
                HaToken = "test-token",
                DefaultMessageTemplate = defaultTemplate,
                PublicUrl = publicUrl,
                SharePageUrl = sharePageUrl,
            }),
            NullLogger<LinkService>.Instance);
    }

    public async Task<TemporaryLink> SeedLinkAsync(
        DateTimeOffset? validFrom = null,
        DateTimeOffset? validUntil = null,
        int maxUses = 1,
        int usageCount = 0,
        LinkStatus status = LinkStatus.Active,
        string? customMessage = null,
        string? recipientPhone = "+15551234567")
    {
        var now = DateTimeOffset.UtcNow;
        var link = new TemporaryLink
        {
            Token = new TokenGenerator().GenerateSecureToken(),
            Name = "Test link",
            Actions = "[]",
            ValidFrom = validFrom ?? now.AddHours(-1),
            ValidUntil = validUntil ?? now.AddHours(1),
            MaxUses = maxUses,
            UsageCount = usageCount,
            RecipientPhoneNumber = recipientPhone,
            CustomMessage = customMessage,
            CreatedBy = "test",
            Status = status,
            WebhookId = "temp_link_test",
            CloudhookId = "cloud_test",
            CloudhookUrl = "https://hooks.nabu.casa/temp_link_test",
        };
        Db.TemporaryLinks.Add(link);
        await Db.SaveChangesAsync();
        return link;
    }

    public Task<List<LinkUsageAudit>> AuditsForAsync(Guid linkId) =>
        Db.LinkUsageAudits.Where(a => a.TemporaryLinkId == linkId)
            .OrderBy(a => a.Timestamp).ToListAsync();

    public void Dispose()
    {
        Db.Dispose();
        _connection.Dispose();
    }
}
