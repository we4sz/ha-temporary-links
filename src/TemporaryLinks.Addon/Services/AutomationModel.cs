using System.Text.Json;
using TemporaryLinks.Addon.Configuration;

namespace TemporaryLinks.Addon.Services;

/// <summary>
/// The one description of the home-side trigger the add-on arms: how it is built, and how to
/// tell whether a trigger already standing in the home still embodies it.
///
/// The trigger never runs a link's real actions. It always reports the attempt to the add-on:
/// inside the validity window it announces a use (<see cref="TriggeredEvent"/>), outside it
/// announces a refusal (<see cref="BlockedEvent"/>). The home therefore still decides in/out of
/// window — it just no longer swallows the attempt, so every refusal stays auditable.
/// </summary>
public static class AutomationModel
{
    /// <summary>Announced by the home when a trigger fires inside the validity window.</summary>
    public const string TriggeredEvent = "temp_link_triggered";

    /// <summary>Announced by the home when a trigger fires outside the validity window.
    /// The add-on audits the refusal and never claims a use for it.</summary>
    public const string BlockedEvent = "temp_link_blocked";

    /// <summary>True when links are shared through a confirm page, which fires the trigger with
    /// an explicit POST — the gesture preview bots never make.</summary>
    public static bool AcceptsPost(AddonConfiguration config) =>
        !string.IsNullOrWhiteSpace(config.SharePageUrl) ||
        !string.IsNullOrWhiteSpace(config.PublicUrl);

    /// <summary>The home-evaluated template that decides whether a fired trigger is inside the
    /// link's validity window.</summary>
    public static string WindowTemplate(DateTimeOffset validFrom, DateTimeOffset validUntil) =>
        $"{{{{ as_datetime('{validFrom.UtcDateTime:yyyy-MM-dd'T'HH:mm:ss}+00:00') <= now() " +
        $"and now() <= as_datetime('{validUntil.UtcDateTime:yyyy-MM-dd'T'HH:mm:ss}+00:00') }}}}";

    /// <summary>The automation config the add-on POSTs to the home for a link.</summary>
    public static object BuildAutomation(
        string token,
        string linkName,
        DateTimeOffset validFrom,
        DateTimeOffset validUntil,
        bool acceptsPost)
    {
        var webhookId = WebhookIdFor(token);
        var eventData = new
        {
            token,
            link_name = linkName,
            webhook_id = webhookId,
        };

        return new
        {
            id = webhookId,
            alias = $"Temp Link: {linkName}",
            description =
                $"Webhook handler for temporary link: {linkName}. " +
                $"Valid from {validFrom:u} to {validUntil:u}",
            trigger = new[]
            {
                new
                {
                    platform = "webhook",
                    webhook_id = webhookId,
                    // With a confirm page in play (shared or self-hosted), only the page's
                    // explicit form POST fires the trigger — preview bots only ever GET.
                    allowed_methods = acceptsPost ? new[] { "POST" } : new[] { "GET" },
                    local_only = false,
                },
            },
            // One action, a choose: in-window announces the use, otherwise announces the
            // refusal. No top-level condition — a refused attempt must still reach the add-on.
            action = new object[]
            {
                new
                {
                    choose = new object[]
                    {
                        new
                        {
                            conditions = new object[]
                            {
                                new
                                {
                                    condition = "template",
                                    value_template = WindowTemplate(validFrom, validUntil),
                                },
                            },
                            sequence = new object[]
                            {
                                new { @event = TriggeredEvent, event_data = eventData },
                            },
                        },
                    },
                    @default = new object[]
                    {
                        new { @event = BlockedEvent, event_data = eventData },
                    },
                },
            },
            mode = "single",
        };
    }

    /// <summary>The automation/webhook id a link's token owns.</summary>
    public static string WebhookIdFor(string token) => $"temp_link_{token}";

    /// <summary>
    /// Whether an automation already stored in the home still matches the CURRENT enforcement
    /// model: the tracking-events-only choose, this link's window, and the gesture the current
    /// sharing mode arms. Anything older (a v1.0 automation embedding the link's real actions,
    /// a top-level window condition that swallows refusals, a stale method) fails to match and
    /// must be re-armed. Home Assistant stores the config back with plural keys, so both
    /// spellings are accepted on read-back.
    /// </summary>
    public static bool MatchesCurrentModel(
        JsonElement stored, string webhookId, string windowTemplate, bool acceptsPost)
    {
        if (stored.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        // A top-level condition belongs to the old model, where the home swallowed refusals.
        if (HasEntries(stored, "conditions") || HasEntries(stored, "condition"))
        {
            return false;
        }

        if (!TryArray(stored, "triggers", "trigger", out var triggers) ||
            triggers.GetArrayLength() != 1)
        {
            return false;
        }

        var trigger = triggers[0];
        if (Text(trigger, "webhook_id") != webhookId)
        {
            return false;
        }

        if (!trigger.TryGetProperty("allowed_methods", out var methods) ||
            methods.ValueKind != JsonValueKind.Array ||
            methods.GetArrayLength() != 1 ||
            methods[0].GetString() != (acceptsPost ? "POST" : "GET"))
        {
            return false;
        }

        if (!TryArray(stored, "actions", "action", out var actions) ||
            actions.GetArrayLength() != 1)
        {
            return false;
        }

        var step = actions[0];
        if (step.ValueKind != JsonValueKind.Object ||
            !step.TryGetProperty("choose", out var choose) ||
            choose.ValueKind != JsonValueKind.Array ||
            choose.GetArrayLength() != 1)
        {
            return false;
        }

        var option = choose[0];
        if (!TryArray(option, "conditions", "condition", out var conditions) ||
            conditions.GetArrayLength() != 1 ||
            Text(conditions[0], "value_template") != windowTemplate)
        {
            return false;
        }

        if (!TryArray(option, "sequence", "sequence", out var sequence) ||
            sequence.GetArrayLength() != 1 ||
            Text(sequence[0], "event") != TriggeredEvent)
        {
            return false;
        }

        return TryArray(step, "default", "default", out var fallback) &&
               fallback.GetArrayLength() == 1 &&
               Text(fallback[0], "event") == BlockedEvent;
    }

    private static bool TryArray(
        JsonElement element, string plural, string singular, out JsonElement array)
    {
        array = default;
        if (element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }
        if (!element.TryGetProperty(plural, out var found) &&
            !element.TryGetProperty(singular, out found))
        {
            return false;
        }
        if (found.ValueKind != JsonValueKind.Array)
        {
            return false;
        }
        array = found;
        return true;
    }

    private static bool HasEntries(JsonElement element, string name) =>
        element.TryGetProperty(name, out var found) &&
        (found.ValueKind != JsonValueKind.Array || found.GetArrayLength() > 0) &&
        found.ValueKind != JsonValueKind.Null;

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out var found) &&
        found.ValueKind == JsonValueKind.String
            ? found.GetString()
            : null;
}
