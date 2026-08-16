using System.Text.Json;
using System.Text.Json.Nodes;

namespace TemporaryLinks.Addon.Services;

/// <summary>
/// Brings a link's actions to exactly the contract execution enforces, at creation time.
///
/// Accepted equivalent forms — notably the home's own automation syntax for a service call
/// (<c>service</c> instead of <c>action</c>, a top-level <c>entity_id</c> instead of
/// <c>target.entity_id</c>) — are rewritten to the canonical form. Anything execution would
/// refuse is refused here, with an explanation, so a link accepted at creation can never
/// later fail on the FORM of its actions (and burn a use doing it).
/// </summary>
public static class ActionsNormalizer
{
    /// <summary>Validates and rewrites <paramref name="actionsJson"/> to the canonical
    /// contract. Throws <see cref="InvalidOperationException"/> with an operator-readable
    /// reason for anything execution would refuse.</summary>
    public static string Normalize(string? actionsJson)
    {
        if (string.IsNullOrWhiteSpace(actionsJson))
        {
            throw new InvalidOperationException(
                "Actions are required: expected a JSON array of service calls, " +
                "e.g. [{\"action\":\"lock.unlock\",\"target\":{\"entity_id\":\"lock.front_door\"}}].");
        }

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(actionsJson);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Actions are not valid JSON: {ex.Message}");
        }

        if (root is not JsonArray array)
        {
            throw new InvalidOperationException(
                "Actions must be a JSON array of service calls, e.g. " +
                "[{\"action\":\"lock.unlock\",\"target\":{\"entity_id\":\"lock.front_door\"}}].");
        }

        var normalized = new JsonArray();
        for (var i = 0; i < array.Count; i++)
        {
            normalized.Add(NormalizeAction(array[i], i + 1));
        }

        return normalized.ToJsonString();
    }

    private static JsonObject NormalizeAction(JsonNode? node, int position)
    {
        if (node is not JsonObject action)
        {
            throw new InvalidOperationException(
                $"Action {position} must be an object with an \"action\" of the form " +
                "\"domain.service\".");
        }

        // Home Assistant's own automation syntax calls it "service"; execution wants "action".
        var service = Text(action, "action") ?? Text(action, "service");
        if (string.IsNullOrWhiteSpace(service) || !service.Contains('.'))
        {
            throw new InvalidOperationException(
                $"Action {position} must have an \"action\" (or \"service\") of the form " +
                "\"domain.service\", e.g. \"lock.unlock\".");
        }

        var result = new JsonObject { ["action"] = service };

        JsonObject? target = null;
        if (action.TryGetPropertyValue("target", out var rawTarget) && rawTarget is not null)
        {
            if (rawTarget is not JsonObject targetObject)
            {
                throw new InvalidOperationException(
                    $"Action {position} has a \"target\" that is not an object — expected " +
                    "{\"entity_id\": \"...\"}.");
            }
            target = (JsonObject)targetObject.DeepClone();
        }

        // A top-level entity_id (again the home's automation syntax) is a target.
        if (action.TryGetPropertyValue("entity_id", out var entityId) && entityId is not null)
        {
            target ??= new JsonObject();
            target["entity_id"] ??= entityId.DeepClone();
        }

        if (target is not null)
        {
            result["target"] = target;
        }

        if (action["data"] is { } data)
        {
            if (data is not JsonObject)
            {
                throw new InvalidOperationException(
                    $"Action {position} has a \"data\" that is not an object.");
            }
            result["data"] = data.DeepClone();
        }

        // Anything else the operator wrote rides along untouched — execution ignores it, and
        // silently dropping an operator's work would be worse than carrying it.
        foreach (var property in action)
        {
            if (property.Key is "action" or "service" or "entity_id" or "target" or "data")
            {
                continue;
            }
            result[property.Key] = property.Value?.DeepClone();
        }

        return result;
    }

    private static string? Text(JsonObject action, string name) =>
        action.TryGetPropertyValue(name, out var value) &&
        value is JsonValue jsonValue &&
        jsonValue.TryGetValue<string>(out var text)
            ? text
            : null;
}
