using System.Diagnostics.CodeAnalysis;

namespace Curia.Tests.Shared;

/// <summary>
/// The 300 ms client timeout of the unheard probes (errata G55, R11.77) for every request except the token step. A plain <c>HttpClient.Timeout</c> also ends the token
/// request against a stub, so a stalled machine turns an unheard row (the write was sent) into a not-sent row (the token was not got) and the probe flakes; this handler
/// applies the 300 ms to the write and the reads and lets the token request take as long as the client's own (long) timeout. It ends a request the way <c>HttpClient.Timeout</c>
/// does: a <see cref="TaskCanceledException"/> holding a <see cref="TimeoutException"/>, the caller's token untouched.
/// </summary>
internal sealed class ShortTimeoutHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
{
    internal static readonly TimeSpan Short = TimeSpan.FromMilliseconds(300);

    /// <summary>A client whose requests but the token step time out after 300 ms.</summary>
    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "The HttpClient owns the handler chain and disposes it with itself.")]
    internal static HttpClient Client(Uri forum) => new(new ShortTimeoutHandler(new HttpClientHandler())) { BaseAddress = forum, Timeout = TimeSpan.FromSeconds(30) };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.Equals(request.RequestUri?.AbsolutePath, "/oauth/token", StringComparison.Ordinal))
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(Short);
        try
        {
            return await base.SendAsync(request, limit.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && limit.IsCancellationRequested)
        {
            throw new TaskCanceledException($"The request was canceled due to the configured timeout of {Short.TotalSeconds} seconds elapsing.", new TimeoutException());
        }
    }
}
