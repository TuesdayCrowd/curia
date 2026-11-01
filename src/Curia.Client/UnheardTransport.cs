namespace Curia.Client;

/// <summary>
/// The one place that decides, from a transport failure of a signed write, whether the request provably was not sent or is unheard, and the one that builds the
/// refusal for each (errata G55, R11.77, D106). <b>The only source file that names the transport's error values.</b> A request is unsent only when the transport
/// names a failure that precedes the request being written (name resolution, the connection, TLS, a proxy tunnel); every other failure, a failure that names none and
/// a client timeout are unheard: a false "unheard" costs an agent one recheck that says <c>unknown</c>, a false "heard" costs a second post. Nothing here retries,
/// waits or asks the Forum (R11.64).
/// </summary>
internal static class UnheardTransport
{
    /// <summary>The refusal for a transport failure raised while the request for <paramref name="url"/> was sent.</summary>
    internal static Refusal Failed(HttpRequestException failure, string url)
    {
        ArgumentNullException.ThrowIfNull(failure);
        ArgumentNullException.ThrowIfNull(url);

        var detail = $"{url}: {failure.Message}";
        return ProvablyNotSent(failure.HttpRequestError)
            ? new Refusal(RefusalKind.Transport, 0, ClientErrors.Transport(detail))
            : Heard(detail);
    }

    /// <summary>The refusal for the client's own timeout: the request was sent and no answer was read.</summary>
    internal static Refusal TimedOut(string url, string message)
    {
        ArgumentNullException.ThrowIfNull(url);
        ArgumentNullException.ThrowIfNull(message);
        return Heard($"{url}: timed out ({message})");
    }

    private static Refusal Heard(string detail) => new(RefusalKind.Transport, 0, ClientErrors.Unheard(detail), Unheard: true);

    private static bool ProvablyNotSent(HttpRequestError error) =>
        error is HttpRequestError.NameResolutionError or HttpRequestError.ConnectionError or HttpRequestError.SecureConnectionError or HttpRequestError.ProxyTunnelError;
}
