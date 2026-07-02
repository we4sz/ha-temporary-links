using System.Text.RegularExpressions;
using TemporaryLinks.Addon.Services;
using Xunit;

namespace TemporaryLinks.Addon.Tests;

public class TokenGeneratorProofTests
{
    // Proves app::E1.S5.A1 — 32 chars, URL-safe alphabet, no padding.
    [Fact]
    public void Token_is_32_urlsafe_characters()
    {
        var generator = new TokenGenerator();
        var urlSafe = new Regex("^[A-Za-z0-9_-]{32}$");

        for (var i = 0; i < 500; i++)
        {
            var token = generator.GenerateSecureToken();
            Assert.Matches(urlSafe, token);
        }
    }

    // Proves app::E1.S5.A2 — tokens do not repeat across many generations.
    [Fact]
    public void Tokens_do_not_repeat()
    {
        var generator = new TokenGenerator();
        var seen = new HashSet<string>();

        for (var i = 0; i < 10_000; i++)
        {
            Assert.True(seen.Add(generator.GenerateSecureToken()),
                "token generator produced a duplicate token");
        }
    }
}
