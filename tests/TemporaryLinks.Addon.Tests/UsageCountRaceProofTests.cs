using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TemporaryLinks.Addon.Configuration;
using TemporaryLinks.Addon.Data;
using TemporaryLinks.Addon.Models;
using TemporaryLinks.Addon.Services;
using Xunit;

namespace TemporaryLinks.Addon.Tests;

/// <summary>
/// Two independent handlers over ONE database — the shape a real burst has, and the only shape
/// that can catch a stale write-back: each handler holds its own view of the link, so a handler
/// that writes its own idea of the usage count clobbers what the other one claimed.
/// </summary>
public sealed class TwoHandlerFixture : IDisposable
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"temporarylinks-race-{Guid.NewGuid():N}.db");

    private readonly List<ApplicationDbContext> _contexts = [];

    public FakeHomeAssistantService Ha { get; } = new();

    public TwoHandlerFixture()
    {
        using var context = NewContext();
        context.Database.EnsureCreated();
    }

    public ApplicationDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite($"Data Source={_dbPath}")
            .Options;
        var context = new ApplicationDbContext(options);
        _contexts.Add(context);
        return context;
    }

    /// <summary>An independent handler: its own context (its own view of the link), the same
    /// database and the same home.</summary>
    public LinkService NewHandler(out ApplicationDbContext context)
    {
        context = NewContext();
        return new LinkService(
            context,
            new TokenGenerator(),
            new FakeTwilioService(),
            Ha,
            Options.Create(new AddonConfiguration
            {
                HaUrl = "http://ha.test:8123",
                HaToken = "test-token",
                DefaultMessageTemplate = "{link}",
            }),
            NullLogger<LinkService>.Instance);
    }

    public async Task<TemporaryLink> SeedLinkAsync(int maxUses)
    {
        await using var context = NewContext();
        var now = DateTimeOffset.UtcNow;
        var token = new TokenGenerator().GenerateSecureToken();
        var link = new TemporaryLink
        {
            Token = token,
            Name = "Race link",
            Actions = "[]",
            ValidFrom = now.AddHours(-1),
            ValidUntil = now.AddHours(1),
            MaxUses = maxUses,
            UsageCount = 0,
            CreatedBy = "test",
            Status = LinkStatus.Active,
            WebhookId = AutomationModel.WebhookIdFor(token),
            CloudhookId = "cloud_test",
            CloudhookUrl = $"https://hooks.nabu.casa/{AutomationModel.WebhookIdFor(token)}",
            LastTriggerProcessedAt = now,
        };
        context.TemporaryLinks.Add(link);
        await context.SaveChangesAsync();
        return link;
    }

    public async Task<TemporaryLink> ReadStoredAsync(Guid id)
    {
        await using var context = NewContext();
        return await context.TemporaryLinks.AsNoTracking().FirstAsync(l => l.Id == id);
    }

    public void Dispose()
    {
        foreach (var context in _contexts)
        {
            context.Dispose();
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }
    }
}

public class UsageCountRaceProofTests
{
    // Proves app::E7.S3.A1 — the usage count survives concurrent handlers with no lost update.
    // Both handlers load the link while it stands at zero uses; the atomic claim is what each
    // one counts by, so the second claim sees the first, and the count the row ends at is the
    // number of uses that actually happened. (A handler that wrote its own idea of the count
    // back would leave 1 here — and hand out a third use on a two-use link.)
    [Fact]
    public async Task Concurrent_handlers_never_lose_a_claimed_use()
    {
        using var fixture = new TwoHandlerFixture();
        var link = await fixture.SeedLinkAsync(maxUses: 2);

        var first = fixture.NewHandler(out var firstContext);
        var second = fixture.NewHandler(out var secondContext);

        // Both handlers have the link in hand before either claims — the real interleaving.
        _ = await firstContext.TemporaryLinks.FirstAsync(l => l.Id == link.Id);
        _ = await secondContext.TemporaryLinks.FirstAsync(l => l.Id == link.Id);

        var firstResult = await first.ExecuteLinkAsync(link.Token, null, null);
        var secondResult = await second.ExecuteLinkAsync(link.Token, null, null);

        Assert.Equal(LinkExecutionStatus.Success, firstResult.Status);
        Assert.Equal(LinkExecutionStatus.Success, secondResult.Status);

        var stored = await fixture.ReadStoredAsync(link.Id);
        Assert.Equal(2, stored.UsageCount);
        Assert.Equal(2, fixture.Ha.ExecutedActionsCount);

        // Retired exactly at the allowance: the second use is the last one.
        Assert.Equal(LinkStatus.Used, stored.Status);
        Assert.Contains(AutomationModel.WebhookIdFor(link.Token), fixture.Ha.DeletedAutomations);
    }

    // Proves app::E7.S1.A2 / app::E7.S3.A1 — the same race on a ONE-use link: the second
    // handler's claim finds the allowance gone, so the actions run exactly once and the count
    // never passes the allowance.
    [Fact]
    public async Task Concurrent_handlers_never_over_consume_a_single_use_link()
    {
        using var fixture = new TwoHandlerFixture();
        var link = await fixture.SeedLinkAsync(maxUses: 1);

        var first = fixture.NewHandler(out var firstContext);
        var second = fixture.NewHandler(out var secondContext);
        _ = await firstContext.TemporaryLinks.FirstAsync(l => l.Id == link.Id);
        _ = await secondContext.TemporaryLinks.FirstAsync(l => l.Id == link.Id);

        var firstResult = await first.ExecuteLinkAsync(link.Token, null, null);
        var secondResult = await second.ExecuteLinkAsync(link.Token, null, null);

        Assert.Equal(LinkExecutionStatus.Success, firstResult.Status);
        Assert.Equal(LinkExecutionStatus.AlreadyUsed, secondResult.Status);
        Assert.Equal(1, fixture.Ha.ExecutedActionsCount);

        var stored = await fixture.ReadStoredAsync(link.Id);
        Assert.Equal(1, stored.UsageCount);
        Assert.Equal(LinkStatus.Used, stored.Status);
    }
}
