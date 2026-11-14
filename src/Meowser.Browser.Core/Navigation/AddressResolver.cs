namespace Meowser.Browser.Core.Navigation;

public static class AddressResolver
{
    private const string SearchEndpoint = "https://www.google.com/search?q=";

    public static Uri Resolve(string input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var value = input.Trim();
        if (string.IsNullOrWhiteSpace(value))
            return new Uri("https://www.google.com");

        if (Uri.TryCreate(value, UriKind.Absolute, out var absolute) &&
            (absolute.Scheme == Uri.UriSchemeHttp || absolute.Scheme == Uri.UriSchemeHttps))
            return absolute;

        if (!value.Contains(' ') &&
            (value.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
             value.StartsWith("localhost:", StringComparison.OrdinalIgnoreCase) ||
             value.Contains('.')) &&
            Uri.TryCreate($"https://{value}", UriKind.Absolute, out var host))
            return host;

        return new Uri(SearchEndpoint + Uri.EscapeDataString(value));
    }
}
