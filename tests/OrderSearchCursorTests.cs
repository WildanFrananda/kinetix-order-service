using System.Buffers.Binary;
using System.Buffers.Text;
using System.Security.Cryptography;
using Kinetix.OrderService.Application.Search;

namespace Kinetix.OrderService.Tests;

public class OrderSearchCursorTests {
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";

    private static readonly OrderSearchCursor Cursor = new(
        new DateTime(2026, 11, 10, 3, 4, 5, DateTimeKind.Utc).AddTicks(1230),
        Guid.Parse("5c0f2a8e-9d41-4b7a-8e3c-1f6d2b9a7e05")
    );

    private static string Forged(byte version, long ticks, Guid id) {
        var position = new byte[25];
        position[0] = version;
        BinaryPrimitives.WriteInt64BigEndian(position.AsSpan(1, 8), ticks);
        id.TryWriteBytes(position.AsSpan(9, 16), bigEndian: true, out _);

        return Base64Url.EncodeToString([.. position, .. SHA256.HashData(position)[..4]]);
    }

    [Fact]
    public void ACursorReadsBackAsThePositionItWasMadeFrom() {
        Assert.True(OrderSearchCursor.TryDecode(Cursor.Encode(), out var decoded));

        Assert.Equal(Cursor.CreatedAt.Ticks, decoded.CreatedAt.Ticks);
        Assert.Equal(DateTimeKind.Utc, decoded.CreatedAt.Kind);
        Assert.Equal(Cursor.Id, decoded.Id);
    }

    [Fact]
    public void ACursorIsShortAndSafeInAUrlUnescaped() {
        var encoded = Cursor.Encode();

        Assert.Equal(OrderSearchCursor.EncodedLength, encoded.Length);
        Assert.Equal(39, encoded.Length);
        Assert.All(encoded, c => Assert.Contains(c, Alphabet));
    }

    [Fact]
    public void ChangingAnyOneCharacterOfACursorIsRefused() {
        var encoded = Cursor.Encode();

        for (var at = 0; at < encoded.Length; at++) {
            var replacement = Alphabet[(Alphabet.IndexOf(encoded[at]) + 1) % Alphabet.Length];
            var tampered = string.Concat(encoded.AsSpan(0, at), replacement.ToString(), encoded.AsSpan(at + 1));

            Assert.False(OrderSearchCursor.TryDecode(tampered, out _), $"a change at {at} was read as {tampered}");
        }
    }

    [Fact]
    public void ACursorCutShortLengthenedOrPaddedIsRefused() {
        var encoded = Cursor.Encode();

        Assert.False(OrderSearchCursor.TryDecode(encoded[..^1], out _));
        Assert.False(OrderSearchCursor.TryDecode(encoded + "A", out _));
        Assert.False(OrderSearchCursor.TryDecode(encoded + "=", out _));
        Assert.False(OrderSearchCursor.TryDecode(encoded[..^1] + "=", out _));
        Assert.False(OrderSearchCursor.TryDecode(" " + encoded[1..], out _));
        Assert.False(OrderSearchCursor.TryDecode(encoded.Replace('-', '+').Replace('_', '/')[..^1] + "+", out _));
        Assert.False(OrderSearchCursor.TryDecode(string.Empty, out _));
        Assert.False(OrderSearchCursor.TryDecode(null, out _));
    }

    [Fact]
    public void ACursorOfAnotherVersionIsRefusedEvenWhenItsCheckHolds() {
        var otherVersion = Forged(2, Cursor.CreatedAt.Ticks, Cursor.Id);
        var sameVersion = Forged(OrderSearchCursor.Version, Cursor.CreatedAt.Ticks, Cursor.Id);

        Assert.False(OrderSearchCursor.TryDecode(otherVersion, out _));
        Assert.True(OrderSearchCursor.TryDecode(sameVersion, out _));
    }

    [Theory]
    [InlineData(long.MaxValue)]
    [InlineData(-1L)]
    public void ACursorNamingNoInstantIsRefused(long ticks) {
        Assert.False(OrderSearchCursor.TryDecode(Forged(OrderSearchCursor.Version, ticks, Cursor.Id), out _));
    }
}
