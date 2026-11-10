using System.Buffers.Binary;
using System.Buffers.Text;
using System.Security.Cryptography;

namespace Kinetix.OrderService.Application.Search;

public readonly record struct OrderSearchCursor(DateTime CreatedAt, Guid Id) {
    public const byte Version = 1;

    private const int TicksLength = sizeof(long);
    private const int IdLength = 16;
    private const int PositionLength = 1 + TicksLength + IdLength;
    private const int CheckLength = 4;
    private const int EncodedByteLength = PositionLength + CheckLength;

    public static readonly int EncodedLength = Base64Url.GetEncodedLength(EncodedByteLength);

    public string Encode() {
        Span<byte> bytes = stackalloc byte[EncodedByteLength];
        WritePosition(bytes[..PositionLength]);
        Check(bytes[..PositionLength]).CopyTo(bytes[PositionLength..]);

        return Base64Url.EncodeToString(bytes);
    }

    public static bool TryDecode(string? text, out OrderSearchCursor cursor) {
        cursor = default;

        if (text is null || text.Length != EncodedLength || !text.All(IsBase64UrlCharacter)) {
            return false;
        }

        Span<byte> bytes = stackalloc byte[EncodedByteLength];
        if (!TryDecodeBytes(text, bytes)) {
            return false;
        }

        var position = bytes[..PositionLength];
        if (position[0] != Version || !Check(position).AsSpan().SequenceEqual(bytes[PositionLength..])) {
            return false;
        }

        var ticks = BinaryPrimitives.ReadInt64BigEndian(position.Slice(1, TicksLength));
        if (ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks) {
            return false;
        }

        var decoded = new OrderSearchCursor(
            new DateTime(ticks, DateTimeKind.Utc),
            new Guid(position.Slice(1 + TicksLength, IdLength), bigEndian: true)
        );

        if (!string.Equals(decoded.Encode(), text, StringComparison.Ordinal)) {
            return false;
        }

        cursor = decoded;
        return true;
    }

    private static bool TryDecodeBytes(string text, Span<byte> bytes) {
        try {
            return Base64Url.TryDecodeFromChars(text, bytes, out var written) && written == EncodedByteLength;
        } catch (FormatException) {
            return false;
        }
    }

    private void WritePosition(Span<byte> position) {
        position[0] = Version;
        BinaryPrimitives.WriteInt64BigEndian(position.Slice(1, TicksLength), CreatedAt.Ticks);
        Id.TryWriteBytes(position.Slice(1 + TicksLength, IdLength), bigEndian: true, out _);
    }

    private static byte[] Check(ReadOnlySpan<byte> position) => SHA256.HashData(position)[..CheckLength];

    private static bool IsBase64UrlCharacter(char c) =>
        c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_';
}
