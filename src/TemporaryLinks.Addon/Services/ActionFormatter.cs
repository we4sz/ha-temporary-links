using System.Text.Json;

namespace TemporaryLinks.Addon.Services;

/// <summary>Turns a link's raw actions JSON into human-readable summaries for the UI,
/// so operators never have to read JSON in a table cell.</summary>
public static class ActionFormatter
{
    public readonly record struct ActionSummary(string Service, string? Target);

    public static IReadOnlyList<ActionSummary> Summarize(string? actionsJson)
    {
        if (string.IsNullOrWhiteSpace(actionsJson))
        {
            return [];
        }

        List<ActionSummary> result = [];
        try
        {
            using var doc = JsonDocument.Parse(actionsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            foreach (var action in doc.RootElement.EnumerateArray())
            {
                if (action.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var service = action.TryGetProperty("action", out var a) ? a.GetString()
                    : action.TryGetProperty("service", out var s) ? s.GetString()
                    : null;
                if (string.IsNullOrEmpty(service))
                {
                    continue;
                }

                string? target = null;
                if (action.TryGetProperty("target", out var t) &&
                    t.ValueKind == JsonValueKind.Object &&
                    t.TryGetProperty("entity_id", out var e))
                {
                    target = e.ValueKind == JsonValueKind.Array
                        ? string.Join(", ", e.EnumerateArray().Select(x => x.GetString()))
                        : e.GetString();
                }

                result.Add(new ActionSummary(service, target));
            }
        }
        catch (JsonException)
        {
            return [];
        }

        return result;
    }
}
