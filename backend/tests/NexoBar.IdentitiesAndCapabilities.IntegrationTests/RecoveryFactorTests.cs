using NexoBar.IdentitiesAndCapabilities;

namespace NexoBar.IdentitiesAndCapabilities.IntegrationTests;

public sealed class RecoveryFactorTests
{
    [Fact]
    public void Canonical_32_byte_base64url_factor_is_accepted_without_normalization()
    {
        var representation = Factor(32);

        Assert.True(RecoveryFactor.TryParse(representation, out var factor));
        Assert.Equal(representation, factor!.Representation);
    }

    [Theory]
    [InlineData(31)]
    [InlineData(33)]
    public void Wrong_byte_length_is_rejected(int byteLength) =>
        Assert.False(RecoveryFactor.TryParse(Factor(byteLength), out _));

    [Theory]
    [InlineData("")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA!")]
    [InlineData(" AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA ")]
    public void Non_canonical_or_malformed_representation_is_rejected(string representation) =>
        Assert.False(RecoveryFactor.TryParse(representation, out _));

    private static string Factor(int byteLength) =>
        Convert.ToBase64String(Enumerable.Range(0, byteLength)
                .Select(value => (byte)value)
                .ToArray())
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
}
