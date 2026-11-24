using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;

namespace Curia.Tests.Shared;

/// <summary>
/// PR 9 (errata G76) probes' one seam over a Forum's answer: a handler in front of another that edits every post object it finds in a JSON response (an object holding
/// <c>post_id</c> and <c>provenance</c>: a single read, a thread, a search hit's <c>post</c>, an inbox row), by post id, so a probe serves a member the stub does not know
/// (<c>answers</c>, <c>revises</c>, <c>question_revised</c>, <c>accepted_answer.accepted_at</c>) or one it serves in a shape the stub cannot, and the oracle is the JSON the probe wrote.
/// </summary>
internal sealed class ProvenanceRewriter(HttpMessageHandler inner) : DelegatingHandler(inner)
{
    /// <summary>Edits by post id; each receives the whole post object and may change its <c>provenance</c>, <c>kind</c> or any other member.</summary>
    internal Dictionary<string, Action<JsonObject>> Edits { get; } = new(StringComparer.Ordinal);

    /// <summary>Applied to every post object after the by-id edit (for a probe that serves one shape on every row).</summary>
    internal Action<JsonObject>? EditAll { get; set; }

    /// <summary>The paths of the requests answered, in order (for a probe that counts requests).</summary>
    internal List<string> Paths { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Paths.Add(request.RequestUri?.AbsolutePath ?? string.Empty);
        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode || response.Content.Headers.ContentType?.MediaType is not "application/json") return response;

        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(text);
        }
        catch (System.Text.Json.JsonException)
        {
            return response;
        }

        if (root is null || !Walk(root)) return response;

        response.Content = new StringContent(root.ToJsonString(), Encoding.UTF8, "application/json");
        return response;
    }

    private bool Walk(JsonNode node)
    {
        var edited = false;
        switch (node)
        {
            case JsonObject o:
                if (o["post_id"] is JsonValue id && id.TryGetValue<string>(out var postId) && o["provenance"] is JsonObject)
                {
                    if (Edits.TryGetValue(postId, out var edit))
                    {
                        edit(o);
                        edited = true;
                    }

                    if (EditAll is { } all)
                    {
                        all(o);
                        edited = true;
                    }
                }

                foreach (var member in o.ToList())
                    if (member.Value is not null && Walk(member.Value)) edited = true;
                break;
            case JsonArray a:
                foreach (var item in a)
                    if (item is not null && Walk(item)) edited = true;
                break;
            default:
                break;
        }

        return edited;
    }
}
