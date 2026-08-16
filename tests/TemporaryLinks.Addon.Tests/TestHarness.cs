using System.Text.Json;
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
    public List<string> DeletedCloudhooks { get; } = [];
    public List<string> ExecutedActions { get; } = [];
    private int _executedActions;
    public int ExecutedActionsCount => Volatile.Read(ref _executedActions);
    public Exception? ThrowOnDelete { get; set; }
    public Exception? ThrowOnCloudhook { get; set; }
    public Exception? ThrowOnExecuteActions { get; set; }
    public Exception? ThrowOnCreateAutomation { get; set; }

    /// <summary>What the home currently stores per automation id, for the re-arm check.
    /// An id that is absent behaves like an automation the home does not have.</summary>
    public Dictionary<string, string> StoredAutomations { get; } = [];

    /// <summary>What the home reports as each automation's last run.</summary>
    public Dictionary<string, DateTimeOffset> LastTriggered { get; } = [];

    public Task ExecuteActionsAsync(
        string actionsJson, CancellationToken cancellationToken = default)
    {
        if (ThrowOnExecuteActions != null)
            throw ThrowOnExecuteActions;
        ExecutedActions.Add(actionsJson);
        Interlocked.Increment(ref _executedActions);
        return Task.CompletedTask;
    }

    public Task<string> CreateWebhookAutomationAsync(
        string token, string linkName, string actionsJson,
        DateTimeOffset validFrom, DateTimeOffset validUntil,
        CancellationToken cancellationToken = default)
    {
        if (ThrowOnCreateAutomation != null)
            throw ThrowOnCreateAutomation;
        var id = AutomationModel.WebhookIdFor(token);
        CreatedAutomations.Add(id);
        ArmedWindows.Add((id, validFrom, validUntil));
        StoredAutomations[id] = JsonSerializer.Serialize(
            AutomationModel.BuildAutomation(token, linkName, validFrom, validUntil));
        return Task.FromResult(id);
    }

    public Task<bool> DeleteWebhookAutomationAsync(
        string automationId, CancellationToken cancellationToken = default)
    {
        if (ThrowOnDelete != null)
            throw ThrowOnDelete;
        DeletedAutomations.Add(automationId);
        StoredAutomations.Remove(automationId);
        return Task.FromResult(true);
    }

    public Task<JsonElement?> TryGetAutomationConfigAsync(
        string automationId, CancellationToken cancellationToken = default)
    {
        if (!StoredAutomations.TryGetValue(automationId, out var json))
            return Task.FromResult<JsonElement?>(null);
        return Task.FromResult<JsonElement?>(
            JsonDocument.Parse(json).RootElement.Clone());
    }

    public Task<IReadOnlyDictionary<string, DateTimeOffset>> GetAutomationLastTriggeredAsync(
        CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyDictionary<string, DateTimeOffset>>(
            new Dictionary<string, DateTimeOffset>(LastTriggered));

    public Task<CloudhookResult> CreateCloudhookAsync(
        string webhookId, CancellationToken cancellationToken = default)
    {
        if (ThrowOnCloudhook != null)
            throw ThrowOnCloudhook;
        return Task.FromResult(new CloudhookResult(
            webhookId, $"cloud_{webhookId}", $"https://hooks.nabu.casa/{webhookId}"));
    }

    public Task DeleteCloudhookAsync(
        string webhookId, CancellationToken cancellationToken = default)
    {
        DeletedCloudhooks.Add(webhookId);
        return Task.CompletedTask;
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
    private readonly AddonConfiguration _config;

    public ApplicationDbContext Db { get; }
    public FakeHomeAssistantService Ha { get; } = new();
    public FakeTwilioService Twilio { get; } = new();
    public LinkService Service { get; }

    public LinkServiceHarness(
        string defaultTemplate =
            "Your temporary access link: {link}\nValid from {start_time} to {end_time}",
        string? publicUrl = null,
        string? sharePageUrl = null,
        ITokenGenerator? tokenGenerator = null)
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(_connection)
            .Options;
        Db = new ApplicationDbContext(options);
        Db.Database.EnsureCreated();

        _config = new AddonConfiguration
        {
            HaUrl = "http://ha.test:8123",
            HaToken = "test-token",
            DefaultMessageTemplate = defaultTemplate,
            PublicUrl = publicUrl,
            SharePageUrl = sharePageUrl,
        };

        Service = new LinkService(
            Db, tokenGenerator ?? new TokenGenerator(), Twilio, Ha, Options.Create(_config),
            NullLogger<LinkService>.Instance);
    }

    public async Task<TemporaryLink> SeedLinkAsync(
        DateTimeOffset? validFrom = null,
        DateTimeOffset? validUntil = null,
        int maxUses = 1,
        int usageCount = 0,
        LinkStatus status = LinkStatus.Active,
        string? customMessage = null,
        string? recipientPhone = "+15551234567",
        bool? triggerAcceptsPost = null,
        DateTimeOffset? lastTriggerProcessedAt = null,
        string? webhookId = null,
        bool armInFakeHome = false)
    {
        var now = DateTimeOffset.UtcNow;
        var token = new TokenGenerator().GenerateSecureToken();
        var link = new TemporaryLink
        {
            Token = token,
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
            WebhookId = webhookId ?? AutomationModel.WebhookIdFor(token),
            CloudhookId = "cloud_test",
            CloudhookUrl = "https://hooks.nabu.casa/temp_link_test",
            TriggerAcceptsPost = triggerAcceptsPost,
            LastTriggerProcessedAt = lastTriggerProcessedAt,
        };
        Db.TemporaryLinks.Add(link);
        await Db.SaveChangesAsync();

        if (armInFakeHome)
        {
            Ha.StoredAutomations[link.WebhookId] = JsonSerializer.Serialize(
                AutomationModel.BuildAutomation(
                    link.Token, link.Name, link.ValidFrom, link.ValidUntil));
            Ha.CreatedAutomations.Clear();
            Ha.ArmedWindows.Clear();
        }

        return link;
    }

    /// <summary>Puts the current trigger model, armed for the OLD one-tap gesture, in the fake
    /// home — a link issued before the confirm page became the only sharing mode.</summary>
    public void ArmLegacyGetGesture(TemporaryLink link) =>
        Ha.StoredAutomations[link.WebhookId] = JsonSerializer.Serialize(
            AutomationModel.BuildAutomation(
                link.Token, link.Name, link.ValidFrom, link.ValidUntil))
            .Replace("\"POST\"", "\"GET\"");

    public Task<List<LinkUsageAudit>> AuditsForAsync(Guid linkId) =>
        Db.LinkUsageAudits.Where(a => a.TemporaryLinkId == linkId)
            .OrderBy(a => a.Timestamp).ToListAsync();

    public void Dispose()
    {
        Db.Dispose();
        _connection.Dispose();
    }
}
