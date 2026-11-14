using System.Buffers.Binary;
using Gog.Protocols.Pb;
using Google.Protobuf;

namespace Goggles.Core.Galaxy;

/// <summary>
/// Reads and writes Galaxy's wire framing: a big-endian <c>u16</c> header length, a protobuf
/// <c>gog.protocols.pb.Header</c>, then <c>Header.size</c> bytes of body.
/// </summary>
/// <remarks>
/// Captured from no documentation — the layout follows Comet's implementation and the CC0 protocol
/// definitions vendored in <c>Galaxy/Protocols</c>, and is to be confirmed against a real game.
/// </remarks>
public static class GalaxyFraming
{
    /// <summary>
    /// The largest body accepted. The header's size field is attacker-controlled input on a socket, so
    /// a garbage value must not become a multi-gigabyte allocation. Real bodies are small.
    /// </summary>
    public const int MaxPayloadSize = 16 * 1024 * 1024;

    private static readonly MessageParser<Header> HeaderParser = Header.Parser.WithExtensionRegistry(
        [Response.Extensions.Rseq, Response.Extensions.Code]);

    /// <summary>
    /// Reads the next message, or returns null if the peer closed the connection between messages.
    /// </summary>
    /// <exception cref="EndOfStreamException">The connection closed partway through a message.</exception>
    /// <exception cref="InvalidDataException">The header is not valid protobuf, or the body is too large.</exception>
    public static async Task<GalaxyMessage?> ReadAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        var prefix = new byte[sizeof(ushort)];
        var read = await stream.ReadAtLeastAsync(prefix, prefix.Length, throwOnEndOfStream: false, cancellationToken)
            .ConfigureAwait(false);

        if (read == 0)
        {
            return null;
        }

        if (read < prefix.Length)
        {
            throw new EndOfStreamException("The connection closed partway through a message.");
        }

        var headerBytes = new byte[BinaryPrimitives.ReadUInt16BigEndian(prefix)];
        await stream.ReadExactlyAsync(headerBytes, cancellationToken).ConfigureAwait(false);

        Header header;
        try
        {
            header = HeaderParser.ParseFrom(headerBytes);
        }
        catch (InvalidProtocolBufferException exception)
        {
            throw new InvalidDataException("A Galaxy message header is not valid protobuf.", exception);
        }

        if (header.Size > MaxPayloadSize)
        {
            throw new InvalidDataException(
                $"A Galaxy message claims a {header.Size}-byte body; the limit is {MaxPayloadSize}.");
        }

        var payload = new byte[header.Size];
        await stream.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);

        return new GalaxyMessage
        {
            Sort = header.Sort,
            Type = header.Type,
            Sequence = header.HasOseq ? header.Oseq : null,
            RespondsTo = header.HasExtension(Response.Extensions.Rseq)
                ? header.GetExtension(Response.Extensions.Rseq)
                : null,
            Status = header.HasExtension(Response.Extensions.Code)
                ? (GalaxyStatus)(int)header.GetExtension(Response.Extensions.Code)
                : null,
            Payload = payload,
        };
    }

    public static ValueTask WriteAsync(
        Stream stream,
        GalaxyMessage message,
        CancellationToken cancellationToken = default) =>
        stream.WriteAsync(Encode(message), cancellationToken);

    /// <summary>Encodes a message exactly as it goes on the wire, length prefix included.</summary>
    public static byte[] Encode(GalaxyMessage message)
    {
        var header = new Header
        {
            Sort = message.Sort,
            Type = message.Type,
            Size = (uint)message.Payload.Length,
        };

        if (message.Sequence is { } sequence)
        {
            header.Oseq = sequence;
        }

        if (message.RespondsTo is { } respondsTo)
        {
            header.SetExtension(Response.Extensions.Rseq, respondsTo);
        }

        if (message.Status is { } status)
        {
            header.SetExtension(Response.Extensions.Code, (Response.Types.Status)(int)status);
        }

        var headerLength = header.CalculateSize();
        if (headerLength > ushort.MaxValue)
        {
            throw new InvalidOperationException("A Galaxy message header cannot exceed 65535 bytes.");
        }

        var buffer = new byte[sizeof(ushort) + headerLength + message.Payload.Length];
        BinaryPrimitives.WriteUInt16BigEndian(buffer, (ushort)headerLength);
        header.WriteTo(buffer.AsSpan(sizeof(ushort), headerLength));
        message.Payload.Span.CopyTo(buffer.AsSpan(sizeof(ushort) + headerLength));
        return buffer;
    }
}
