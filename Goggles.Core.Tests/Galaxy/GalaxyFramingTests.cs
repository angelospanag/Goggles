using System.Buffers.Binary;
using Gog.Protocols.Pb;
using Goggles.Core.Galaxy;
using Google.Protobuf;

namespace Goggles.Core.Tests.Galaxy;

public class GalaxyFramingTests
{
    [Fact]
    public async Task A_message_survives_encoding_and_reading_back()
    {
        var original = new GalaxyMessage
        {
            Sort = 1,
            Type = 4,
            Sequence = 12,
            RespondsTo = 7,
            Status = GalaxyStatus.NotFound,
            Payload = new byte[] { 1, 2, 3, 250 },
        };

        using var stream = new MemoryStream(GalaxyFraming.Encode(original));
        var read = await GalaxyFraming.ReadAsync(stream, TestContext.Current.CancellationToken);

        Assert.NotNull(read);
        Assert.Equal(original.Sort, read.Sort);
        Assert.Equal(original.Type, read.Type);
        Assert.Equal(original.Sequence, read.Sequence);
        Assert.Equal(original.RespondsTo, read.RespondsTo);
        Assert.Equal(original.Status, read.Status);
        Assert.Equal(original.Payload.ToArray(), read.Payload.ToArray());
    }

    /// <summary>
    /// The first frame Absolute Drift's macOS build (Galaxy SDK in <c>libGalaxy64.dylib</c>) sent to
    /// <c>galaxy-service</c> on 2026-11-14, as captured — client secret already redacted by the capture.
    /// </summary>
    private const string AbsoluteDriftAuthInfo =
        "000808011003182320010a113532343732353639373731343233303239120a5b72656461637465645d28dfb902";

    [Fact]
    public async Task A_frame_captured_from_a_real_game_decodes_as_expected()
    {
        using var stream = new MemoryStream(Convert.FromHexString(AbsoluteDriftAuthInfo));

        var message = await GalaxyFraming.ReadAsync(stream, TestContext.Current.CancellationToken);

        Assert.NotNull(message);
        Assert.Equal("AUTH_INFO_REQUEST", CommunicationMessages.NameOf(message));
        Assert.Equal(1u, message.Sequence);
        var body = global::Galaxy.Protocols.CommunicationService.AuthInfoRequest.Parser.ParseFrom(message.Payload.Span);
        Assert.Equal("52472569771423029", body.ClientId);
        Assert.Equal(40159u, body.GamePid);
        Assert.False(body.HasPlatformType);
        Assert.False(body.HasOpenid);

        // Re-encoding reproduces the game's bytes exactly, so replies are framed the way games frame.
        Assert.Equal(AbsoluteDriftAuthInfo, Convert.ToHexStringLower(GalaxyFraming.Encode(message)));
    }

    [Fact]
    public void The_wire_layout_is_a_big_endian_header_length_then_header_then_body()
    {
        var bytes = GalaxyFraming.Encode(new GalaxyMessage { Sort = 1, Type = 3, Payload = new byte[] { 9, 9 } });

        var headerLength = BinaryPrimitives.ReadUInt16BigEndian(bytes);
        var header = Header.Parser.ParseFrom(bytes.AsSpan(2, headerLength));

        Assert.Equal(1u, header.Sort);
        Assert.Equal(3u, header.Type);
        Assert.Equal(2u, header.Size);
        Assert.Equal(new byte[] { 9, 9 }, bytes[(2 + headerLength)..]);
    }

    [Fact]
    public void Optional_header_fields_are_left_off_when_absent()
    {
        var bytes = GalaxyFraming.Encode(new GalaxyMessage { Sort = 1, Type = 1 });
        var header = Header.Parser.ParseFrom(bytes.AsSpan(2));

        Assert.False(header.HasOseq);
        Assert.False(header.HasExtension(Response.Extensions.Rseq));
        Assert.False(header.HasExtension(Response.Extensions.Code));
    }

    [Fact]
    public async Task Consecutive_messages_are_read_one_at_a_time()
    {
        using var stream = new MemoryStream(
        [
            .. GalaxyFraming.Encode(new GalaxyMessage { Sort = 1, Type = 1, Sequence = 1 }),
            .. GalaxyFraming.Encode(new GalaxyMessage { Sort = 1, Type = 3, Sequence = 2, Payload = new byte[] { 5 } }),
        ]);

        var first = await GalaxyFraming.ReadAsync(stream, TestContext.Current.CancellationToken);
        var second = await GalaxyFraming.ReadAsync(stream, TestContext.Current.CancellationToken);
        var end = await GalaxyFraming.ReadAsync(stream, TestContext.Current.CancellationToken);

        Assert.Equal(1u, first?.Sequence);
        Assert.Equal(2u, second?.Sequence);
        Assert.Equal(new byte[] { 5 }, second?.Payload.ToArray());
        Assert.Null(end);
    }

    [Fact]
    public async Task A_closed_connection_between_messages_reads_as_null()
    {
        using var stream = new MemoryStream();

        Assert.Null(await GalaxyFraming.ReadAsync(stream, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(1)] // half the length prefix
    [InlineData(4)] // part of the header
    [InlineData(-1)] // all but the last body byte
    public async Task A_connection_closed_partway_through_a_message_is_an_error(int keep)
    {
        var bytes = GalaxyFraming.Encode(new GalaxyMessage { Sort = 1, Type = 3, Payload = new byte[] { 1, 2, 3 } });
        using var stream = new MemoryStream(bytes[..(keep > 0 ? keep : bytes.Length + keep)]);

        await Assert.ThrowsAsync<EndOfStreamException>(() =>
            GalaxyFraming.ReadAsync(stream, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task An_absurd_body_size_is_refused_before_anything_is_allocated()
    {
        var header = new Header { Sort = 1, Type = 3, Size = GalaxyFraming.MaxPayloadSize + 1u };
        var headerBytes = header.ToByteArray();
        var prefix = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(prefix, (ushort)headerBytes.Length);
        using var stream = new MemoryStream([.. prefix, .. headerBytes]);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            GalaxyFraming.ReadAsync(stream, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_header_that_is_not_protobuf_is_invalid_data()
    {
        using var stream = new MemoryStream([0, 3, 0xFF, 0xFF, 0xFF]);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            GalaxyFraming.ReadAsync(stream, TestContext.Current.CancellationToken));
    }
}
