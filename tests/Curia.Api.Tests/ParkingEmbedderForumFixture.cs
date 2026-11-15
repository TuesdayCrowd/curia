using System.Security.Cryptography;
using System.Text;
using Curia.Application.Ports;
using Curia.Application.Retrieval;
using Curia.Domain.Primitives;
using Curia.Domain.Search;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit.Sdk;

namespace Curia.Api.Tests;

/// <summary>
/// Register item 286 (errata G71, R9.65): the Forum as <see cref="Program"/> composes it, with the one registered <see cref="ITextEmbedder"/> (the hashed test adapter, the fixture's one explicit door)
/// wrapped in a decorator that can be made to park. R11.92's gate bounds the order of the wait and not its length, so a request that waits at the embedder is the case this fixture builds on demand:
/// a test <see cref="ParkingEmbedder.Arm"/>s one call, waits for it to be reached, does what must happen during the wait (appends a declaration or a moderation record), and
/// <see cref="Park.Release"/>s it.
///
/// <para><b>This is the only place a park exists.</b> A park inside a test body would be a second implementation. The decorator is unarmed by default and passes every call through. An armed call is
/// one call: the first whose role and text match; it signals <see cref="Park.EnteredAsync"/> and disarms the decorator at once, so the accepted post's own indexing, the sweep and any retry never park.</para>
///
/// <para><b>Composition.</b> <c>Program.cs</c> builds <see cref="HybridSearch"/>, <see cref="DuplicateCheck"/> and the <see cref="EmbeddingIndexer"/> each from the container's one
/// <see cref="ITextEmbedder"/>, and nothing else constructs an embedder, so a decorator registered in <c>ConfigureStore</c> parks all three.</para>
/// </summary>
public class ParkingEmbedderForumFixture : ForumFixture
{
    /// <summary>The decorator the host's embedder is. Created with the fixture, so a test reads it before the host is built.</summary>
    internal ParkingEmbedder Embedder { get; } = new(new HashedNGramEmbedder());

    protected override void ConfigureStore(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        base.ConfigureStore(services);
        services.RemoveAll<ITextEmbedder>();
        services.AddSingleton<ITextEmbedder>(Embedder);
    }
}

/// <summary>The parking fixture over the search surface's floor configured at V1 (as <see cref="ConfiguredFloorForumFixture"/> does), for the request that names a floor below it.</summary>
public sealed class ParkingEmbedderConfiguredFloorForumFixture : ParkingEmbedderForumFixture
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        base.ConfigureWebHost(builder);
        builder.UseSetting("Curia:Retrieval:Floors:rest-search", "V1");
    }
}

/// <summary>An armed call: <see cref="EnteredAsync"/> says whether the embedder was reached, <see cref="Release"/> lets it return.</summary>
internal sealed class Park
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal Task ReleaseSignal => _release.Task;

    internal void SignalEntered() => _entered.TrySetResult();

    /// <summary>Waits, bounded, for the armed call to be reached; a park that never parked fails as <c>NOT REACHED</c> and not as a pass or a hang (trap 275).</summary>
    internal async Task EnteredAsync(string what)
    {
        try
        {
            await _entered.Task.WaitAsync(Bound);
        }
        catch (TimeoutException)
        {
            throw new XunitException($"NOT REACHED: the embedder was not asked for {what} within {Bound.TotalSeconds:0} s, so nothing was parked");
        }
    }

    /// <summary>Whether the armed call has been reached, without waiting.</summary>
    internal bool IsEntered => _entered.Task.IsCompleted;

    internal void Release() => _release.TrySetResult();
}

/// <summary>
/// The decorator. Unarmed it is the inner embedder. Armed with a role and a text predicate it parks the first matching call until released, then disarms itself.
/// Honours the caller's cancellation while parked, so a cancelled test leaves no thread waiting. <see cref="Calls"/> is the call log: the role and a hash of the text, never the text.
/// </summary>
internal sealed class ParkingEmbedder(ITextEmbedder inner) : ITextEmbedder
{
    private readonly object _gate = new();
    private (EmbeddingRole Role, Func<string, bool> Text, Park Park)? _armed;
    private readonly List<(EmbeddingRole Role, string TextHash)> _calls = [];

    public EmbeddingModel Model => inner.Model;

    /// <summary>The calls made so far, in order.</summary>
    internal IReadOnlyList<(EmbeddingRole Role, string TextHash)> Calls
    {
        get
        {
            lock (_gate) return [.. _calls];
        }
    }

    /// <summary>Arms the decorator for one call: the first whose role is <paramref name="role"/> and whose text satisfies <paramref name="text"/>.</summary>
    internal Park Arm(EmbeddingRole role, Func<string, bool> text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var park = new Park();
        lock (_gate)
        {
            if (_armed is not null) throw new InvalidOperationException("the embedder is already armed: a second park needs the first to be reached");
            _armed = (role, text, park);
        }

        return park;
    }

    /// <summary>Disarms an armed call that was never reached (a test's clean-up); an armed call already reached has disarmed itself.</summary>
    internal void Disarm()
    {
        lock (_gate) _armed = null;
    }

    public async Task<Result<Embedding>> EmbedAsync(string text, EmbeddingRole role, CancellationToken cancellationToken)
    {
        Park? parked = null;
        lock (_gate)
        {
            _calls.Add((role, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..12]));
            if (_armed is { } armed && armed.Role == role && armed.Text(text))
            {
                parked = armed.Park;
                _armed = null;
            }
        }

        if (parked is not null)
        {
            parked.SignalEntered();
            await parked.ReleaseSignal.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        return await inner.EmbedAsync(text, role, cancellationToken).ConfigureAwait(false);
    }
}
