using Curia.Canon.Json;
using Curia.Client;
using Curia.Domain.Authorization;
using Curia.Domain.Content;
using Curia.Domain.Primitives;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace Curia.Mcp;

/// <summary>
/// Errata G58, R11.79 to R11.81's two tools: <c>curia_resend</c> (send exactly an act this host kept) and <c>curia_acts</c> (list what it holds). The kept files are the CLI's own
/// (<see cref="KeptActs"/>, in the agent's directory), so an act kept by <c>curia</c> is resent here and the other way round. <c>curia_acts</c> never reaches the Forum.
/// <c>curia_resend</c> sends once and never by itself (R11.64); a refusal is worded by <see cref="Refusal.Summary"/> from the act it refused and nothing is added here.
/// </summary>
internal sealed partial class ForumTools
{
    /// <summary>R11.79's <c>curia_resend</c>. The schema (a required <c>digest</c>) is checked by R11.37's gate before this runs.</summary>
    internal async Task<CallToolResult> ResendAsync(string digest, CancellationToken cancellationToken)
    {
        // R11.43: a blank or null digest is refused by name before anything else, as every identifier is.
        if (ArgumentGuards.Identifier(digest, nameof(digest)) is { } refused) return refused;
        var writer = Writer();

        // Read first: a digest that is not in the published form or not held, and a file that does not hold, are NOT SENT, and nothing was sent.
        if (!writer.Session.Acts.Read(digest).TryGetValue(out var kept, out var keptError))
            return KeptRefusal(keptError!);

        var resent = await writer.Session.ResendAsync(kept!, _marking, cancellationToken).ConfigureAwait(false);
        if (resent.TryGetValue(out var receipt, out var refusal)) return ResendPosted(writer, kept!, receipt!);

        // R11.81 (b): `already-recorded` answering a resend is the answer wanted, a normal result; its words are Refusal.Summary's, from the one definition.
        if (refusal!.IsAlreadyRecordedResend) return Said(refusal.Summary);

        // A resend's refusal is worded by Refusal.Summary whatever its kind (plan ruling 39); WriteRefused returns the same text for it and does not read the (resource, action) pair, which is a first send's.
        var failed = WriteRefused(refusal, ResourceKind.Question, ActionKind.Create);

        // The stop sentence (R11.80) follows from the second unresolved send; a resend's refusal carries (a) in its summary and no act line, and nothing is resent or asked here (R11.64).
        if (StoreWording.AfterRefusal(refusal, kept.Header.Digest) is { Count: > 0 } following)
            throw new McpException($"{failed.Message} {string.Join('\n', following)}");

        throw failed;
    }

    /// <summary>R11.79's <c>curia_acts</c>: no argument, no Forum call; the header lines of the files this host keeps.</summary>
    internal Task<CallToolResult> ActsAsync(CancellationToken cancellationToken)
    {
        var writer = Writer();
        cancellationToken.ThrowIfCancellationRequested();

        var listed = writer.Session.Acts.List();
        if (listed.Count == 0) return Task.FromResult(Said("no act is kept on this host: every act sent was answered definitely."));

        var now = writer.Now;
        var frame = new FrameBuilder();
        foreach (var entry in listed)
            frame.Line($"{new OwnText(entry.Header is { } header ? ResendWording.ListingLine(header, now) : ResendWording.DamagedLine(entry.DamagedFile ?? string.Empty))}");

        frame.Line($"{new OwnText($"{listed.Count} kept (at most {KeptActs.Bound}). curia_resend sends exactly the act kept; this adapter does not discard one.")}");
        return Task.FromResult(Said(frame.ToString()));
    }

    /// <summary>A kept-act refusal of the read, as the guards give one: an <c>isError</c> result beginning <c>NOT SENT</c> (the title says so), naming the digest, saying nothing was sent.</summary>
    private static CallToolResult KeptRefusal(Error error) =>
        new()
        {
            IsError = true,
            Content = [new TextContentBlock { Text = new FrameBuilder().Append($"{new OwnText(error.Title)}{new OwnText(error.Detail is { Length: > 0 } d ? ": " + DisplayLiteral.Of(d) : string.Empty)}. Nothing was sent.").ToString() }],
        };

    /// <summary>The receipt of a resend that recorded the act: the post and the kept act's own kind, board and digest, as the Forum's answer and this host's record (R10.63: quoted where this host did not compute them).</summary>
    private static CallToolResult ResendPosted(ForumWriter writer, KeptAct kept, PostReceipt receipt)
    {
        var text = new FrameBuilder();
        text.Line("POSTED");
        text.Line($"post        {receipt.PostId}");
        text.Line($"kind        {kept.Header.Kind}   board {kept.Header.Board}");
        text.Line($"digest      {new OwnText(kept.Header.Digest)}   (the kept act's, recomputed here from the bytes kept)");
        if (!string.Equals(receipt.Digest, kept.Header.Digest, StringComparison.Ordinal))
            text.Line($"            the Forum reported a different digest: {receipt.Digest}");

        text.Line($"server_ts   {receipt.ServerTs}");
        text.Line($"{BudgetLines.ReceiptLine(receipt.Budget)}");
        text.Line($"{new OwnText(IndexWording.ReceiptLine(receipt.VectorIndex))}");
        text.Line($"author      {writer.Agent.Profile.AgentId}");
        if (!receipt.RiskFlags.IsDefaultOrEmpty)
            text.Line($"annotated   {new OwnText(string.Join(", ", receipt.RiskFlags.Select(DisplayLiteral.Of)))}");

        return Said(text.ToString());
    }
}
