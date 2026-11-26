using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Text.Json;
using Curia.Domain.Primitives;
using Microsoft.Extensions.Primitives;

namespace Curia.Api;

/// <summary>
/// The Forum's one name (errata G82, R5.26, R11.109): the configured public origin, which every <c>htu</c>, every <c>aud</c>, the token's
/// <c>iss</c>, the metadata and the Reader Contract URL are built from. The request's <c>Host</c>, scheme, path base and forwarded headers
/// are read for none of them (ORG13 holds that in IL).
///
/// <para><b>The value is its own canonical form, and nothing else is accepted.</b> Every comparison downstream is ordinal and the client signs
/// what <see cref="Uri"/> makes of the URL it dials, so the origin is a string both ends must spell identically. <see cref="Parse"/> refuses
/// rather than corrects: a silent correction would make the configuration say one thing and the Forum another, which is the defect this type
/// closes. The refusal names the canonical form it would have accepted, where that can be said without the offending part.</para>
///
/// <para><b>Grammar</b>, written out so the parser exercises no judgement: scheme <c>http</c> or <c>https</c>; host one of DNS labels
/// (<c>[a-z0-9-]</c>, 1 to 63 each, none starting or ending with a hyphen, joined by single dots, 253 at most, no trailing dot), four decimal
/// octets without leading zeros, or a bracketed IPv6 literal that equals <see cref="Uri"/>'s own rendering; no user or password part; a port, if
/// present, 1 to 65535 without leading zeros and not the scheme's default; nothing after the authority. The client's own builder is then asked
/// the same question as a second, independent check: <c>new Uri(new Uri(origin), "/oauth/token").ToString()</c> must equal
/// <c>origin + "/oauth/token"</c>.</para>
/// </summary>
public sealed class PublicOrigin
{
    /// <summary>The configuration key the origin is read from first.</summary>
    public const string ConfigurationKey = "Curia:PublicOrigin";

    /// <summary>The environment variable the origin is read from when the configuration key is absent.</summary>
    public const string EnvironmentVariable = "CURIA_PUBLIC_ORIGIN";

    private const string ErrorType = "curia/config/public-origin";
    private const string ErrorTitle = "The public origin is not usable";
    private const int MaxQuoted = 200;

    private readonly string _authority;
    private readonly string? _authorityWithDefaultPort;

    private PublicOrigin(string value, string authority, string? authorityWithDefaultPort)
    {
        Value = value;
        _authority = authority;
        _authorityWithDefaultPort = authorityWithDefaultPort;
    }

    /// <summary>The origin, as configured and as canonical: <c>scheme://host[:port]</c>.</summary>
    public string Value { get; }

    /// <summary>The URL a credential is compared with: the origin and the request's path, no query and no fragment.</summary>
    [SuppressMessage("Design", "CA1055:URI-like return values should not be strings", Justification = "The URL is compared byte-for-byte with the string a credential names (R5.26); a Uri would normalise it.")]
    public string UrlOf(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Value + request.Path.ToUriComponent();
    }

    /// <summary>The Reader Contract's well-known URL (R10.21).</summary>
    [SuppressMessage("Design", "CA1055:URI-like return values should not be strings", Justification = "The URL is compared byte-for-byte with the string a credential names (R5.26); a Uri would normalise it.")]
    public string ReaderContractUrl() => Value + Curia.Domain.Serving.ReaderContract.WellKnownPath;

    /// <summary>
    /// R5.27's comparison: the <c>Host</c> header exactly as received (one value, never decoded, trimmed or de-dotted), ASCII case-insensitively
    /// equal to the origin's authority, an explicit default port counting as the same as none when the origin has none.
    /// </summary>
    public bool Serves(StringValues hostHeader)
    {
        if (hostHeader.Count != 1) return false;
        var wire = hostHeader[0];
        if (wire is null) return false;
        return string.Equals(wire, _authority, StringComparison.OrdinalIgnoreCase)
            || (_authorityWithDefaultPort is not null && string.Equals(wire, _authorityWithDefaultPort, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Reads the origin from its two sources, the configuration key first and then the variable, refusing a replaced key.</summary>
    public static Result<PublicOrigin> Read(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        foreach (var replaced in new[] { "Curia:Issuer", "Curia:Audience" })
        {
            if (configuration[replaced] is not null)
                return Result<PublicOrigin>.Fail(new Error(ErrorType, ErrorTitle,
                    $"{replaced} is no longer read: the Forum's name is {ConfigurationKey} (or {EnvironmentVariable}), one value for the issuer, the audience and every URL a credential is compared with. Remove {replaced}."));
        }

        var text = configuration[ConfigurationKey];
        if (text is null) text = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (string.IsNullOrWhiteSpace(text))
            return Result<PublicOrigin>.Fail(new Error(ErrorType, ErrorTitle,
                $"No public origin configured. Set {ConfigurationKey} or {EnvironmentVariable} to the origin this Forum is reached at, for example \"https://forum.example\". " +
                "The Forum does not run without a name: every token, proof and contract URL is bound to it (R11.109)."));

        return Parse(text);
    }

    /// <summary>Parses an origin that must already be its own canonical form; anything else is refused.</summary>
    public static Result<PublicOrigin> Parse(string? text)
    {
        if (text is null) return Result<PublicOrigin>.Fail(Refusal("no value was given", null));

        var failure = Check(text, out var origin);
        if (failure is null) return Result<PublicOrigin>.Ok(origin!);

        return Result<PublicOrigin>.Fail(Refusal(failure, text));
    }

    private static Error Refusal(string why, string? text)
    {
        if (text is null) return new Error(ErrorType, ErrorTitle, $"{ConfigurationKey} {why}.");

        // A value that carries an @ is never quoted: it may hold a password, and the start log is read by more people than the secret is (R10.27's shape).
        if (text.Contains('@', StringComparison.Ordinal))
            return new Error(ErrorType, ErrorTitle, $"{ConfigurationKey} is refused: the value has a user or password part, or an @, which an origin does not carry. Configure the origin as scheme://host[:port] with no user part.");

        var detail = $"{ConfigurationKey} is refused: {why}. The value was {Quote(text)}.";
        var canonical = CanonicalHint(text);
        if (canonical is not null) detail += $" {ConfigurationKey} must read exactly {Quote(canonical)}.";
        else detail += $" {ConfigurationKey} must be an origin in its canonical form, for example \"https://forum.example\": lower-case ASCII host (an internationalized name in its xn-- form), no trailing dot, no path, query or fragment, the scheme's default port omitted.";
        return new Error(ErrorType, ErrorTitle, detail);
    }

    /// <summary>A configured value as a display literal: quoted, control and non-ASCII characters escaped, cut at a fixed length. Never markup.</summary>
    private static string Quote(string text)
    {
        var shown = text.Length > MaxQuoted ? text[..MaxQuoted] + "..." : text;
        return JsonSerializer.Serialize(shown);
    }

    /// <summary>The canonical spelling the value would have been accepted as, when it can be computed without the offending part; else null.</summary>
    [SuppressMessage("Globalization", "CA1308:Normalize strings to uppercase", Justification = "The canonical form of a host is lower case (R11.109); this builds the spelling the refusal names.")]
    private static string? CanonicalHint(string text)
    {
        string scheme;
        string rest;
        if (text.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) { scheme = "http"; rest = text["http://".Length..]; }
        else if (text.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) { scheme = "https"; rest = text["https://".Length..]; }
        else return null;

        var end = rest.IndexOfAny(['/', '?', '#']);
        var authority = end < 0 ? rest : rest[..end];
        if (authority.Length == 0) return null;

        string host = authority;
        var port = string.Empty;
        if (!authority.StartsWith('['))
        {
            var colon = authority.LastIndexOf(':');
            if (colon >= 0) { host = authority[..colon]; port = authority[(colon + 1)..]; }
            host = host.TrimEnd('.').ToLowerInvariant();
        }
        else
        {
            var close = authority.IndexOf(']', StringComparison.Ordinal);
            if (close < 0) return null;
            host = authority[..(close + 1)].ToLowerInvariant();
            port = authority[(close + 1)..].TrimStart(':');
        }

        if (port == (scheme == "https" ? "443" : "80")) port = string.Empty;
        var candidate = scheme + "://" + host + (port.Length > 0 ? ":" + port : string.Empty);
        return Check(candidate, out _) is null ? candidate : null;
    }

    /// <summary>The grammar and the round trip. Null when the text is its own canonical form; otherwise why it is not.</summary>
    private static string? Check(string text, out PublicOrigin? origin)
    {
        origin = null;
        string scheme;
        string authority;
        if (text.StartsWith("http://", StringComparison.Ordinal)) { scheme = "http"; authority = text["http://".Length..]; }
        else if (text.StartsWith("https://", StringComparison.Ordinal)) { scheme = "https"; authority = text["https://".Length..]; }
        else return "the scheme must be http or https, in lower case, followed by ://";

        if (authority.Contains('@', StringComparison.Ordinal)) return "the value has a user or password part";
        if (authority.Length == 0) return "there is no host";

        foreach (var c in authority)
        {
            if (c is '/' or '?' or '#') return "an origin has no path, query or fragment, and no trailing slash";
            if (c <= ' ' || c > '~') return "the value holds a space, control or non-ASCII character";
        }

        string host;
        string portText = string.Empty;
        if (authority[0] == '[')
        {
            var close = authority.IndexOf(']', StringComparison.Ordinal);
            if (close < 0) return "the IPv6 literal is not closed";
            host = authority[..(close + 1)];
            var after = authority[(close + 1)..];
            if (after.Length > 0)
            {
                if (after[0] != ':') return "text follows the IPv6 literal that is not a port";
                portText = after[1..];
                if (portText.Length == 0) return "the port is empty";
            }

            var ipv6 = host.AsSpan(1, host.Length - 2);
            if (ipv6.Contains('%') || !IPAddress.TryParse(ipv6, out var address) || address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6)
                return "the IPv6 literal is not valid";
            if (!Uri.TryCreate("http://" + host, UriKind.Absolute, out var rendered) || !string.Equals(rendered.Host, host, StringComparison.Ordinal))
                return "the IPv6 literal is not in the form a client signs (lower case, zeros compressed)";
        }
        else
        {
            var colon = authority.IndexOf(':', StringComparison.Ordinal);
            if (colon >= 0)
            {
                host = authority[..colon];
                portText = authority[(colon + 1)..];
                if (portText.Length == 0) return "the port is empty";
                if (portText.Contains(':', StringComparison.Ordinal)) return "the authority holds more than one colon";
            }
            else host = authority;

            var hostFailure = CheckName(host);
            if (hostFailure is not null) return hostFailure;
        }

        string? defaultPortAuthority = null;
        var defaultPort = scheme == "https" ? "443" : "80";
        if (portText.Length > 0)
        {
            if (portText.Any(c => c is < '0' or > '9')) return "the port is not a number";
            if (portText[0] == '0') return "the port has a leading zero or is zero";
            if (portText.Length > 5 || int.Parse(portText, CultureInfo.InvariantCulture) > 65535) return "the port is out of range";
            if (portText == defaultPort) return $"the scheme's default port {defaultPort} is written";
        }
        else defaultPortAuthority = authority + ":" + defaultPort;

        var url = new Uri(new Uri(text), "/oauth/token").ToString();
        if (!string.Equals(url, text + "/oauth/token", StringComparison.Ordinal))
            return "a client that dials it signs a different spelling of it";

        origin = new PublicOrigin(text, authority, defaultPortAuthority);
        return null;
    }

    private static string? CheckName(string host)
    {
        if (host.Length == 0) return "there is no host";
        if (host.Length > 253) return "the host is longer than 253 characters";
        if (host[^1] == '.') return "the host ends in a dot";

        var labels = host.Split('.');
        foreach (var label in labels)
        {
            if (label.Length == 0) return "the host has an empty label";
            if (label.Length > 63) return "a label of the host is longer than 63 characters";
            if (label[0] == '-' || label[^1] == '-') return "a label of the host starts or ends with a hyphen";
            foreach (var c in label)
            {
                if (c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-') continue;
                return c is >= 'A' and <= 'Z'
                    ? "the host is not lower case"
                    : "the host holds a character that is not a letter, digit or hyphen (an internationalized name is configured in its xn-- form)";
            }
        }

        // A dotted-decimal host must be four octets without leading zeros; a number-shaped host that is not one (127.1) is another spelling of an address.
        if (labels.All(l => l.All(char.IsAsciiDigit)))
        {
            if (labels.Length != 4) return "the host is a number that is not four dotted octets";
            foreach (var octet in labels)
                if (octet.Length > 3 || (octet.Length > 1 && octet[0] == '0') || int.Parse(octet, CultureInfo.InvariantCulture) > 255)
                    return "an octet of the host has a leading zero or is above 255";
        }

        return null;
    }
}
