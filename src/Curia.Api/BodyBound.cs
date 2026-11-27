using Curia.Canon.Json;

namespace Curia.Api;

/// <summary>
/// Errata G84 (R6.68 (rev.)): the Forum's own bound on any request body, R6.39's submission size plus one byte. Public, and a type of its own, so that a fact reads the figure and
/// drives the stream without a new <c>InternalsVisibleTo</c> (CS-5).
/// </summary>
public static class RequestBodyBound
{
    /// <summary>
    /// R6.68's figure, read from the one definition of R6.39's cap and never typed a second time (a build-failing fence reads this member's IL). One byte past R6.39's size, so that a body of exactly one byte
    /// past it reaches ADMIT and is refused there with R6.40's slug, and one byte more is refused here.
    /// </summary>
    public static long Cap => (long)AdmitLimits.Default.MaxBytes + 1;
}

/// <summary>
/// The stream the bound lays over a request's body: delivers at most <c>cap</c> bytes, and on the read that would deliver the byte after them throws the host's own <c>413</c>, so a body sent with no
/// declared length (the framing <c>HttpClient</c> never uses by default, and the one a declared-length check cannot see) is cut at the bound whichever reader reads it: the binder, a handler's copy,
/// the form reader or a key act's loop. It never delivers past the bound: a read that would is shortened to the room left, and a read at the bound asks the inner stream for one byte to learn whether
/// the body ends there.
/// </summary>
public sealed class BodyBoundStream(Stream inner, long cap) : Stream
{
    private long _delivered;

    public long Cap { get; } = cap;

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush()
    {
    }

    private static BadHttpRequestException TooLarge() => new("Request body too large.", StatusCodes.Status413PayloadTooLarge);

    /// <summary>The most a read of <paramref name="wanted"/> bytes may ask the inner stream for; zero means the bound is reached and the next byte, if there is one, is the refusal.</summary>
    private int Room(int wanted) => (int)Math.Min(wanted, Cap - _delivered);

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (buffer.IsEmpty) return await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        var room = Room(buffer.Length);
        if (room == 0)
        {
            var probe = new byte[1];
            if (await inner.ReadAsync(probe, cancellationToken).ConfigureAwait(false) > 0) throw TooLarge();
            return 0;
        }

        var read = await inner.ReadAsync(buffer[..room], cancellationToken).ConfigureAwait(false);
        _delivered += read;
        return read;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        if (buffer.IsEmpty) return inner.Read(buffer);
        var room = Room(buffer.Length);
        if (room == 0)
        {
            Span<byte> probe = stackalloc byte[1];
            if (inner.Read(probe) > 0) throw TooLarge();
            return 0;
        }

        var read = inner.Read(buffer[..room]);
        _delivered += read;
        return read;
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>
/// Errata G84 (R6.68 (rev.), R11.33): the one middleware that bounds every request body. A declared length past the bound is refused <c>413</c> before any handler runs, on every method and every path,
/// mapped or not; any other body is read through <see cref="BodyBoundStream"/>, and a <c>BadHttpRequestException</c> that leaves the pipeline while the response is unstarted (this stream's, or the host's own
/// reader's: a body too slow or wrongly framed) is given the status the host chose. The status is composed into R11.33's answer by <c>UseUnreadableRequests</c>, which sits outside this and fires on a
/// response that has no body, so the host's own response writer, which answers a rejected body with nothing in Production and with a page that carries the request's headers in Development, is never reached.
/// </summary>
internal static class BodyBound
{
    public static WebApplication UseBodyBound(this WebApplication app)
    {
        app.Use(async (http, next) =>
        {
            var request = http.Request;
            if (request.ContentLength > RequestBodyBound.Cap)
            {
                http.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
                return;
            }

            request.Body = new BodyBoundStream(request.Body, RequestBodyBound.Cap);
            try
            {
                await next(http).ConfigureAwait(false);
            }
            catch (BadHttpRequestException refused) when (!http.Response.HasStarted)
            {
                http.Response.ContentLength = null;
                http.Response.ContentType = null;
                http.Response.StatusCode = refused.StatusCode;
            }
        });
        return app;
    }
}
