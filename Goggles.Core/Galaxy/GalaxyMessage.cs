namespace Goggles.Core.Galaxy;

/// <summary>
/// One message on a Galaxy socket: the routing fields of its protobuf header, and its still-encoded
/// body. Bodies stay as bytes here because their type depends on <see cref="Sort"/> and
/// <see cref="Type"/>; <see cref="CommunicationMessages"/> knows how to read the ones Goggles handles.
/// </summary>
public sealed record GalaxyMessage
{
    /// <summary>The subprotocol. 1 is the communication service — achievements, stats, auth.</summary>
    public required uint Sort { get; init; }

    /// <summary>Message type within the subprotocol, e.g. 3 for <c>AUTH_INFO_REQUEST</c>.</summary>
    public required uint Type { get; init; }

    /// <summary>The sender's sequence number (<c>oseq</c>); a reply quotes it back as <see cref="RespondsTo"/>.</summary>
    public uint? Sequence { get; init; }

    /// <summary>The <see cref="Sequence"/> of the message this one answers (<c>rseq</c>, header extension 100).</summary>
    public uint? RespondsTo { get; init; }

    /// <summary>A reply's status (header extension 101). Absent means OK.</summary>
    public GalaxyStatus? Status { get; init; }

    public ReadOnlyMemory<byte> Payload { get; init; }
}

/// <summary>Reply status codes, numbered like their HTTP namesakes on the wire.</summary>
public enum GalaxyStatus
{
    Ok = 200,
    Accepted = 202,
    BadRequest = 400,
    Unauthorized = 401,
    Forbidden = 403,
    NotFound = 404,
    Conflict = 409,
    InternalError = 500,
    NotImplemented = 501,
    Unavailable = 503,
}
