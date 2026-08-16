using TemporaryLinks.Addon.Services;
using Xunit;

namespace TemporaryLinks.Addon.Tests;

public class ActionFormatterProofTests
{
    // Proves app::E1.S7.A2 (in part) — the action summary the UI shows is derived from the
    // same structured actions JSON a link stores, so what the operator sees matches what runs.
    [Fact]
    public void Summarizes_service_and_entity_target()
    {
        var summary = ActionFormatter.Summarize(
            "[{\"action\":\"lock.unlock\",\"target\":{\"entity_id\":\"lock.front_door\"}}]");

        var one = Assert.Single(summary);
        Assert.Equal("lock.unlock", one.Service);
        Assert.Equal("lock.front_door", one.Target);
    }

    [Fact]
    public void Summarizes_multiple_actions_and_missing_targets()
    {
        var summary = ActionFormatter.Summarize(
            "[{\"action\":\"scene.turn_on\"},{\"service\":\"light.turn_on\",\"target\":{\"entity_id\":[\"light.a\",\"light.b\"]}}]");

        Assert.Equal(2, summary.Count);
        Assert.Equal("scene.turn_on", summary[0].Service);
        Assert.Null(summary[0].Target);
        Assert.Equal("light.turn_on", summary[1].Service);
        Assert.Equal("light.a, light.b", summary[1].Target);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("not json")]
    [InlineData("{\"action\":\"x\"}")] // object, not an array
    public void Returns_empty_for_unusable_input(string? input)
    {
        Assert.Empty(ActionFormatter.Summarize(input));
    }
}
