using TemporaryLinks.Addon.Services;
using Xunit;

namespace TemporaryLinks.Addon.Tests;

/// <summary>A token generator that always returns the same token — the cheapest way to make
/// the store reject a link (the token is unique by index) after the home already hosts its
/// trigger.</summary>
public sealed class FixedTokenGenerator(string token) : ITokenGenerator
{
    public string GenerateSecureToken(int length = 32) => token;
}

public class CreationCompensationProofTests
{
    // Proves app::E1.S1.A4 — when persisting the link fails AFTER the trigger and its public
    // relay exist, both are taken back out of the home: no orphaned trigger, no half-registered
    // relay, and no link.
    [Fact]
    public async Task Store_failure_after_the_trigger_exists_leaves_nothing_behind()
    {
        var token = "fixed-token-for-collision-000000";
        using var h = new LinkServiceHarness(tokenGenerator: new FixedTokenGenerator(token));
        var now = DateTimeOffset.UtcNow;

        // The first link takes the token; the second creation cannot be stored.
        await h.Service.CreateLinkAsync("Gate", now, now.AddHours(1), null, null, "test", "[]");
        h.Ha.CreatedAutomations.Clear();
        h.Ha.DeletedAutomations.Clear();

        await Assert.ThrowsAnyAsync<Exception>(() =>
            h.Service.CreateLinkAsync("Gate again", now, now.AddHours(1), null, null, "test", "[]"));

        var created = Assert.Single(h.Ha.CreatedAutomations);
        Assert.Contains(created, h.Ha.DeletedAutomations);
        Assert.Contains(created, h.Ha.DeletedCloudhooks);
        Assert.Single(h.Db.TemporaryLinks); // only the first link survives
    }

    // Proves app::E1.S1.A4 — the cloudhook path: the trigger is compensated away, and no
    // cloudhook delete is attempted for a cloudhook that was never created.
    [Fact]
    public async Task Relay_failure_removes_the_trigger_and_leaves_no_link()
    {
        using var h = new LinkServiceHarness();
        h.Ha.ThrowOnCloudhook = new InvalidOperationException("HA Cloud unavailable");
        var now = DateTimeOffset.UtcNow;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            h.Service.CreateLinkAsync("Gate", now, now.AddHours(1), null, null, "test", "[]"));

        var created = Assert.Single(h.Ha.CreatedAutomations);
        Assert.Contains(created, h.Ha.DeletedAutomations);
        Assert.Empty(h.Ha.DeletedCloudhooks);
        Assert.Empty(h.Db.TemporaryLinks);
    }
}
