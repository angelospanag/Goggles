using Galaxy.Protocols.CommunicationService;
using Google.Protobuf;
using Google.Protobuf.Reflection;

namespace Goggles.Core.Galaxy;

/// <summary>
/// Names, decodes and redacts communication-service messages (<see cref="Sort"/> 1) for logging and
/// for capturing test fixtures from real games.
/// </summary>
public static class CommunicationMessages
{
    /// <summary>The subprotocol number of the communication service.</summary>
    public const uint Sort = 1;

    private const string Redacted = "[redacted]";

    // Credentials a game hands over (its client secret, a Steam ticket) or is handed (a refresh
    // token). Matched by field name across every message, so a new message type carrying one is
    // covered without a code change.
    private static readonly HashSet<string> SensitiveFields =
        ["client_secret", "steam_ticket", "refresh_token", "access_token"];

    private static readonly FileDescriptor Protocol = GalaxyProtocolsCommunicationServiceReflection.Descriptor;

    private static readonly EnumDescriptor MessageTypes = Protocol.FindTypeByName<EnumDescriptor>("MessageType");

    /// <summary>
    /// The protocol's name for a message type, e.g. <c>AUTH_INFO_REQUEST</c>; numeric for anything
    /// outside the communication service or not in the vendored definitions.
    /// </summary>
    public static string NameOf(GalaxyMessage message)
    {
        if (message.Sort == Sort
            && MessageTypes.Values.LastOrDefault(value => value.Number == message.Type) is { } value)
        {
            // Types 49 and 50 are aliased (START_OVERLAY_SESSION_* and START_GAME_SESSION_*), and the
            // game-session names come last; that is the meaning Comet observed games using.
            return value.Name;
        }

        return $"sort {message.Sort} type {message.Type}";
    }

    /// <summary>
    /// A one-line description for logs: name, sequence numbers, status and the decoded body as JSON,
    /// with credentials redacted.
    /// </summary>
    public static string Describe(GalaxyMessage message)
    {
        var parts = new List<string> { NameOf(message) };

        if (message.Sequence is { } sequence)
        {
            parts.Add($"seq={sequence}");
        }

        if (message.RespondsTo is { } respondsTo)
        {
            parts.Add($"re={respondsTo}");
        }

        if (message.Status is { } status)
        {
            parts.Add($"status={status}");
        }

        if (TryDecode(message) is { } body)
        {
            RedactFields(body);
            parts.Add(JsonFormatter.Default.Format(body));
        }
        else if (!message.Payload.IsEmpty)
        {
            parts.Add($"<{message.Payload.Length} bytes, not decoded>");
        }

        return string.Join(' ', parts);
    }

    /// <summary>
    /// Returns the message with credentials in its body replaced, for writing captures to disk. A body
    /// that cannot be decoded is returned unchanged — so captures of other subprotocols are not
    /// guaranteed clean and must be inspected before being committed.
    /// </summary>
    public static GalaxyMessage Redact(GalaxyMessage message)
    {
        if (TryDecode(message) is not { } body || !RedactFields(body))
        {
            return message;
        }

        return message with { Payload = body.ToByteArray() };
    }

    /// <summary>
    /// Decodes a body by the naming convention the protocol follows throughout: type
    /// <c>AUTH_INFO_REQUEST</c> carries an <c>AuthInfoRequest</c>. Null when there is no such message
    /// type, or the bytes do not parse as one.
    /// </summary>
    internal static IMessage? TryDecode(GalaxyMessage message)
    {
        if (message.Sort != Sort)
        {
            return null;
        }

        var name = NameOf(message);
        var messageName = string.Concat(name.Split('_').Select(word =>
            word.Length == 0 ? word : char.ToUpperInvariant(word[0]) + word[1..].ToLowerInvariant()));

        if (Protocol.FindTypeByName<MessageDescriptor>(messageName) is not { } descriptor)
        {
            return null;
        }

        try
        {
            return descriptor.Parser.ParseFrom(message.Payload.Span);
        }
        catch (InvalidProtocolBufferException)
        {
            return null;
        }
    }

    /// <summary>Overwrites every non-empty sensitive string field; true if anything changed.</summary>
    private static bool RedactFields(IMessage message)
    {
        var changed = false;
        foreach (var field in message.Descriptor.Fields.InFieldNumberOrder())
        {
            if (field.FieldType == FieldType.String
                && !field.IsRepeated
                && SensitiveFields.Contains(field.Name)
                && field.Accessor.GetValue(message) is string { Length: > 0 })
            {
                field.Accessor.SetValue(message, Redacted);
                changed = true;
            }
            else if (field.FieldType == FieldType.Message
                     && !field.IsRepeated
                     && field.Accessor.HasValue(message)
                     && field.Accessor.GetValue(message) is IMessage nested)
            {
                changed |= RedactFields(nested);
            }
            else if (field.FieldType == FieldType.Message
                     && field.IsRepeated
                     && field.Accessor.GetValue(message) is System.Collections.IList items)
            {
                foreach (var item in items.OfType<IMessage>())
                {
                    changed |= RedactFields(item);
                }
            }
        }

        return changed;
    }
}
