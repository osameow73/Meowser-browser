using Meowser.Browser.Core.Navigation;

namespace Meowser.Browser.Core.Tests;

public sealed class AddressResolverTests
{
    [Theory]
    [InlineData("https://example.com/path", "https://example.com/path")]
    [InlineData("http://example.com", "http://example.com/")]
    [InlineData("example.com", "https://example.com/")]
    [InlineData("localhost:8080", "https://localhost:8080/")]
    public void Resolve_RecognizedAddress_ReturnsWebAddress(string input, string expected) =>
        Assert.Equal(expected, AddressResolver.Resolve(input).AbsoluteUri);

    [Theory]
    [InlineData("two words", "https://www.google.com/search?q=two%20words")]
    [InlineData("", "https://www.google.com/")]
    [InlineData("   ", "https://www.google.com/")]
    public void ResolveSearchInput_ReturnsSearchAddress(string input, string expected) =>
        Assert.Equal(expected, AddressResolver.Resolve(input).AbsoluteUri);

    [Fact]
    public void Resolve_NonWebScheme_DoesNotReturnExecutableProtocol()
    {
        var result = AddressResolver.Resolve("javascript:alert(1)");

        Assert.Equal(Uri.UriSchemeHttps, result.Scheme);
        Assert.Equal("www.google.com", result.Host);
    }
}
