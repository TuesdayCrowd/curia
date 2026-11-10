using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text.Json;
using Curia.Application.Ports;
using Curia.Application.Retrieval;
using Curia.Domain.Primitives;
using Curia.Domain.Search;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Xunit;
using static Curia.Api.Tests.RevisionProbe;

namespace Curia.Api.Tests;

/// <summary>A fixture whose host runs <c>Program.cs</c>'s own embedder composition: the one explicit door to the hashed adapter is closed (errata G65, R11.91).</summary>
public sealed class ProductionEmbedderFixture : ForumFixture
{
    protected override bool ReplacesEmbedderWithTheHashedOne => false;
}

/// <summary>
/// Errata G65 (D10a), SEM20 to SEM24, on the production Forum over a real Postgres and no real model: a Forum that cannot trust its model does not start (R11.91), nothing selects a fallback, the
/// one explicit door is the test fixture's, and a runtime fault of the model is a <c>503</c> with a fixed document and nothing stored (R11.92).
///
/// <para><b>Oracles.</b> A refusal is the host's start throwing, with the configuration key, the file or the canary named in a message of its chain and no path in any; "nothing stored" is the
/// row count of the <c>events</c> table read directly; the served controls are the same hosts with the explicit door open. Each refusing row has a served control beside it.</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
[Collection("forum")]
public sealed class EmbedderBootTests(ProductionEmbedderFixture production, ForumFixture hashed) : IClassFixture<ProductionEmbedderFixture>, IClassFixture<ForumFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string EnvVar = "CURIA_EMBEDDING_MODEL_DIR";

    private static string Chain(Exception thrown)
    {
        var messages = new List<string>();
        for (Exception? e = thrown; e is not null; e = e.InnerException)
            messages.Add(e.Message);
        return string.Join(" | ", messages);
    }

    private static async Task<long> EventCountAsync(ForumFixture fixture)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var count = new NpgsqlCommand("SELECT count(*) FROM events;", connection);
        return (long)(await count.ExecuteScalarAsync(Ct))!;
    }

    /// <summary>Starts the production host with <paramref name="settings"/> and no model-directory environment variable, and returns what the start threw, or <c>null</c> when it started.</summary>
    private static Exception? TryStart(ForumFixture fixture, params (string Key, string? Value)[] settings)
    {
        var before = Environment.GetEnvironmentVariable(EnvVar);
        Environment.SetEnvironmentVariable(EnvVar, null);
        try
        {
            using var host = fixture.WithWebHostBuilder(builder =>
            {
                foreach (var (key, value) in settings)
                    if (value is not null) builder.UseSetting(key, value);
            });
            try
            {
                host.CreateClient().Dispose();
                return null;
            }
#pragma warning disable CA1031 // the probe reads what the start threw, whatever its type
            catch (Exception ex)
#pragma warning restore CA1031
            {
                return ex;
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(EnvVar, before);
        }
    }

    /// <summary>
    /// SEM20. The production composition with no <c>Curia:Embedding:ModelDirectory</c> does not start: the exception names <c>Curia:Embedding:ModelDirectory</c> and <c>CURIA_EMBEDDING_MODEL_DIR</c>, and the
    /// events table gains nothing. Predicted red on the seam: <c>the host started: the production composition registers the hashed embedder</c>.
    /// </summary>
    [Fact]
    public async Task R11_91_SEM20_ABootWithNoModelDirectoryIsRefusedByName()
    {
        var before = await EventCountAsync(production);

        var thrown = TryStart(production);

        Assert.True(thrown is not null, "the host started: the production composition registers the hashed embedder");
        var chain = Chain(thrown!);
        Assert.Contains("Curia:Embedding:ModelDirectory", chain, StringComparison.Ordinal);
        Assert.Contains("CURIA_EMBEDDING_MODEL_DIR", chain, StringComparison.Ordinal);
        Assert.Equal(before, await EventCountAsync(production));
    }

    /// <summary>
    /// SEM20, the served control: the same Forum with the explicit door open boots and states the hashed model on a search, so the refusal above is the composition's and not the fixture's.
    /// </summary>
    [Fact]
    public async Task R11_91_SEM20_TheSameFixtureWithTheExplicitDoorOpenBootsAndServesTheHashedModel()
    {
        var reply = await NameProbe.GetAsync(hashed.Client, "/v1/search?q=anything", ct: Ct);

        Assert.Equal(HttpStatusCode.OK, reply.Status);
        Assert.Equal("hashed-ngram@1", reply.Json.GetProperty("model").GetString());
    }

    private static string JunkDirectory(bool model, bool vocabulary)
    {
        var directory = Path.Combine(Path.GetTempPath(), "curia-sem21-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        if (model) File.WriteAllBytes(Path.Combine(directory, "model.onnx"), new byte[1024]);
        if (vocabulary) File.WriteAllText(Path.Combine(directory, "vocab.txt"), "[PAD]\n[UNK]\n");
        return directory;
    }

    /// <summary>
    /// SEM21. A directory that is not the manifest's is refused before any listener, naming the file and the cause and holding no path: a 1 KB <c>model.onnx</c> with a <c>vocab.txt</c> of the right name
    /// (a digest mismatch), a missing model, a missing vocabulary, a directory that does not exist. Predicted red on the seam: <c>the host started</c>.
    /// </summary>
    [Theory]
    [InlineData("junk-files", "model.onnx", "digest")]
    [InlineData("missing-model", "model.onnx", "missing")]
    [InlineData("missing-vocabulary", "vocab.txt", "missing")]
    [InlineData("missing-directory", "Curia:Embedding:ModelDirectory", "directory")]
    public void R11_91_SEM21_ADirectoryThatIsNotTheManifestsIsRefusedBeforeTheFirstRequest(string variant, string names, string cause)
    {
        var directory = variant switch
        {
            "junk-files" => JunkDirectory(true, true),
            "missing-model" => JunkDirectory(false, true),
            "missing-vocabulary" => JunkDirectory(true, false),
            _ => Path.Combine(Path.GetTempPath(), "curia-sem21-absent-" + Guid.NewGuid().ToString("N")),
        };

        var thrown = TryStart(production, ("Curia:Embedding:ModelDirectory", directory));

        Assert.True(thrown is not null, $"{variant}: the host started");
        var chain = Chain(thrown!);
        Assert.Contains(names, chain, StringComparison.Ordinal);
        Assert.Contains(cause, chain, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(directory, chain, StringComparison.Ordinal);
    }

    /// <summary>
    /// SEM22. Nothing selects a fallback: <c>Curia:Embedding:Provider=hashed</c>, <c>Curia:Retrieval:Embedder=hashed-ngram@1</c> and <c>Curia:Embedding:Model=hashed-ngram</c> beside a bad directory still refuse
    /// the start. Predicted red on the seam: <c>the host started with a fallback configured</c>.
    /// </summary>
    [Fact]
    public void R11_91_SEM22_NoConfigurationSelectsAFallback()
    {
        var thrown = TryStart(
            production,
            ("Curia:Embedding:Provider", "hashed"),
            ("Curia:Retrieval:Embedder", "hashed-ngram@1"),
            ("Curia:Embedding:Model", "hashed-ngram"),
            ("Curia:Embedding:ModelDirectory", JunkDirectory(true, true)));

        Assert.True(thrown is not null, "the host started with a fallback configured");
        Assert.Contains("model.onnx", Chain(thrown!), StringComparison.Ordinal);
    }

    /// <summary>
    /// SEM23. The override is the one explicit door: the fixture's host serves <c>hashed-ngram@1</c> and its <c>ITextEmbedder</c> is the hashed adapter. Control: green on the seam; it holds that the
    /// door SEM20 closes is the fixture's and not production's.
    /// </summary>
    [Fact]
    public void R11_91_SEM23_TheFixtureReplacesTheEmbedderExplicitly()
    {
        Assert.IsType<HashedNGramEmbedder>(hashed.Services.GetRequiredService<ITextEmbedder>());
    }

    // ---- SEM24: a runtime fault ----

    private const string Secret = "/srv/secret-path/model.onnx";

    private sealed class FaultingEmbedder : ITextEmbedder
    {
        public EmbeddingModel Model => new("bge-small-en-v1.5", "1", 384);

        public Task<Result<Embedding>> EmbedAsync(string text, EmbeddingRole role, CancellationToken cancellationToken) =>
            Task.FromResult(Result<Embedding>.Fail(EmbeddingErrors.ModelFault(nameof(InvalidOperationException))));
    }

    /// <summary>
    /// SEM24. A registered embedder that answers <c>ModelFault</c> (the exception it caught had a message holding <c>/srv/secret-path/model.onnx</c>, which the adapter's contract keeps out of the
    /// <c>Error</c>): <c>GET /v1/search</c> is <c>503</c> with the fixed type and no text of the exception; a <c>POST</c> of a question is <c>503</c>, nothing stored; the same hosts with the hashed
    /// embedder serve (the controls). The 503 carries no detail member (the controller's ruling on Task 1's open question: Error.Detail holds the exception's type name for the Forum's log and
    /// the document serves none, R9.52). Green on the seam and after: the routes already map an embedder failure to <c>503</c> before PERSIST through ServerFault.
    /// </summary>
    [Fact]
    public async Task R11_92_SEM24_AModelFaultIsA503WithAFixedDocumentAndNothingStored()
    {
        var actor = await AcceptedAnswerScene.EnrolAsync(hashed, "sem24", Ct);

        using var faulting = hashed.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<ITextEmbedder>();
            services.AddSingleton<ITextEmbedder>(new FaultingEmbedder());
        }));
        using var client = faulting.CreateClient();
        var before = await EventCountAsync(hashed);

        var search = await NameProbe.GetAsync(client, "/v1/search?q=x", ct: Ct);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, search.Status);
        Assert.Equal(EmbeddingErrors.ModelFault().Type, search.Json.GetProperty("type").GetString());
        Assert.DoesNotContain(Secret, search.Body, StringComparison.Ordinal);
        // R9.52: a fault the Forum owns is served with its fixed title and type and no detail member; the exception's type name (the error's Detail) goes to the Forum's log.
        // Every fault the Forum owns serves `"detail":null`, as the store faults do; "no detail" is that member absent or null.
        Assert.True(!search.Json.TryGetProperty("detail", out var detail) || detail.ValueKind == JsonValueKind.Null, "the 503 document carries a detail: " + search.Body);
        Assert.DoesNotContain(nameof(InvalidOperationException), search.Body, StringComparison.Ordinal);

        var sent = await SendViaAsync(hashed, client, actor, actor.Agent.SignQuestion(RevisionProbe.Board(), Unrelated(), "About " + Unrelated(), hashed.Now), null, Ct);
        Assert.Equal((503, EmbeddingErrors.ModelFault().Type), (sent.Status, sent.Type));
        Assert.DoesNotContain(Secret, sent.Body, StringComparison.Ordinal);
        Assert.DoesNotContain(nameof(InvalidOperationException), sent.Body, StringComparison.Ordinal);
        Assert.Equal(before, await EventCountAsync(hashed));

        // The served control: the host with the hashed embedder (this fixture's own) takes the same question and serves the same search.
        var control = await SendAsync(hashed, actor, actor.Agent.SignQuestion(RevisionProbe.Board(), Unrelated(), "About " + Unrelated(), hashed.Now), cancellationToken: Ct);
        Assert.True(control.Status == 201, $"the control question was answered {control.Says}");
        Assert.Equal(HttpStatusCode.OK, (await NameProbe.GetAsync(hashed.Client, "/v1/search?q=x", ct: Ct)).Status);
    }
}
