using Xunit;

namespace TemporaryLinks.Addon.Tests;

public class MessageComposeProofTests
{
    // Proves app::E3.S1.A1 — custom message wins; otherwise the configured default is used.
    [Fact]
    public async Task Custom_message_overrides_default_template()
    {
        using var h = new LinkServiceHarness(defaultTemplate: "default: {link}");
        var custom = await h.SeedLinkAsync(customMessage: "custom: {link}");
        var plain = await h.SeedLinkAsync();

        await h.Service.SendSmsAsync(custom);
        await h.Service.SendSmsAsync(plain);

        Assert.StartsWith("custom: ", h.Twilio.Sent[0].Message);
        Assert.StartsWith("default: ", h.Twilio.Sent[1].Message);
    }

    // Proves app::E3.S1.A2 — every placeholder is replaced with the link's actual values.
    [Fact]
    public async Task Placeholders_are_replaced_with_link_values()
    {
        using var h = new LinkServiceHarness(
            defaultTemplate: "{name}|{link}|{start_time}|{end_time}");
        var link = await h.SeedLinkAsync();

        await h.Service.SendSmsAsync(link);

        var message = h.Twilio.Sent.Single().Message;
        var parts = message.Split('|');
        Assert.Equal(4, parts.Length);
        Assert.Equal(link.Name, parts[0]);
        Assert.Equal(link.CloudhookUrl, parts[1]);
        Assert.Equal(link.ValidFrom.ToLocalTime().ToString("g"), parts[2]);
        Assert.Equal(link.ValidUntil.ToLocalTime().ToString("g"), parts[3]);
        Assert.DoesNotContain("{", message);
    }
}
