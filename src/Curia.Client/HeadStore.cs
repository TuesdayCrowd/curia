using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Curia.Canon.Json;

namespace Curia.Client;

/// <summary>
/// R6.53: the last signed tree head this client verified, per Forum origin.
///
/// <para><b>Why this exists at all.</b> R6.24 names "anyone who retained an old head" as the party
/// who detects a fork. Until this type, no Cūria client retained one, so the detection the whole
/// transparency argument rests on had no detector on the Forum's own client -- plan defect D9, and
/// the reason a consistency check written without this would have nothing to compare against and
/// would pass trivially.</para>
///
/// <para><b>Outside the agent directory, and that is the requirement rather than a preference.</b>
/// Reading requires no identity: R6.19's reader confirms authorship without trusting Forum-supplied
/// results, and nothing about that requires enrolment. A head under <c>agents/&lt;slug&gt;/</c>
/// would be unreachable by the reader who needs it most, and two identities on one machine would
/// hold two views of one log -- the fork the proof exists to find, hidden by the directory layout.
/// So the layout is:</para>
///
/// <code>
/// ~/.curia/logs/&lt;origin&gt;/head.json    0600   the last head whose signature this client verified
/// </code>
///
/// <para><b>At the private mode</b> because an attacker who can rewrite the retained head can
/// re-anchor every proof that follows it -- the same reasoning the signing keys get, applied to the
/// one file that decides whether a fork is visible.</para>
///
/// <para><b>Replacement is not this type's decision.</b> <see cref="Write"/> stores what it is
/// given; R6.23's consistency proof is checked by <see cref="PostVerifier"/> before it calls, and a
/// consistency <i>failure</i> must not call at all. Refreshing on the failure is how a client that
/// detected a fork forgets it, so the guard lives where the verdict is, not here.</para>
/// </summary>
public sealed class HeadStore
{
    private const string HeadFile = "head.json";

    public HeadStore(string root) => Root = root;

    /// <summary>The client's own root: <c>$CURIA_CLIENT_HOME</c>, or <c>~/.curia</c>.</summary>
    public string Root { get; }

    public static HeadStore Default() => new(ProfileStore.DefaultRoot);

    /// <summary>
    /// The directory holding one Forum's retained head. A sibling of <c>agents/</c>, never a child
    /// of one.
    /// </summary>
    public string DirectoryFor(Uri forum) => Path.Combine(Root, "logs", OriginKey(forum));

    /// <summary>
    /// A filesystem-safe name for a Forum origin, and an injective one.
    ///
    /// <para>The readable half is for a human looking at the directory; the eight hex digits are
    /// what make it a key. Sanitising alone is not injective -- <c>a.b</c> and <c>a_b</c> both
    /// become <c>a_b</c> -- and two Forums sharing a directory would share a head, which is the
    /// cross-log confusion this file exists to prevent.</para>
    ///
    /// <para>Scheme, host and port only. A path, a query or a userinfo would split one log's head
    /// across several files, and the Acta is a property of the Forum rather than of the URL a caller
    /// happened to type.</para>
    /// </summary>
    public static string OriginKey(Uri forum)
    {
        ArgumentNullException.ThrowIfNull(forum);

        var origin = forum.GetLeftPart(UriPartial.Authority);
        var digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(origin)))[..8];

        var readable = new StringBuilder(origin.Length);
        foreach (var c in origin)
            readable.Append(char.IsAsciiLetterOrDigit(c) || c is '-' or '.' ? c : '_');

        return string.Create(CultureInfo.InvariantCulture, $"{readable}-{digest}");
    }

    /// <summary>
    /// Whether a head is retained for a Forum, and whether this client can read the one it has.
    ///
    /// <para>Kept apart from <see cref="Read"/>'s null because R6.53 turns on the difference. A
    /// first read has nothing to compare against and stores what it verified; a retained head that
    /// will not parse is evidence this client cannot interpret, and replacing it would discard the
    /// only record of what the log looked like before -- the "client that detected a fork forgets
    /// it" outcome, reached through corruption rather than through a failed proof.</para>
    /// </summary>
    public enum RetainedHead
    {
        /// <summary>No head has been retained for this Forum. The next verification stores one.</summary>
        None,

        /// <summary>A head is retained and readable.</summary>
        Present,

        /// <summary>A head is retained and this client could not read it. Never overwritten.</summary>
        Unreadable,
    }

    /// <summary>Which of the three states this Forum's retained head is in.</summary>
    public RetainedHead State(Uri forum)
    {
        var path = Path.Combine(DirectoryFor(forum), HeadFile);
        if (!File.Exists(path)) return RetainedHead.None;

        return Read(forum) is null ? RetainedHead.Unreadable : RetainedHead.Present;
    }

    /// <summary>
    /// The retained head, or <see langword="null"/> when none has been kept for this Forum <i>or</i>
    /// the one that has cannot be read. Callers that must tell those apart use
    /// <see cref="State"/>.
    ///
    /// <para>Null is a first-read, and <see cref="PostVerifier"/> reports it as
    /// <see cref="CheckOutcome.CouldNotCheck"/> rather than as a passing consistency check. A
    /// consistency assertion with nothing to compare against passes trivially, which is this
    /// project's most-repeated defect; here that shape is refused at the source.</para>
    ///
    /// <para>An unreadable or unparseable file is also null: a corrupt retained head is a head this
    /// client cannot claim to have verified, and treating it as absent puts the caller on the
    /// first-read path rather than reporting a fork nobody detected.</para>
    /// </summary>
    public SignedHeadDocument? Read(Uri forum)
    {
        var path = Path.Combine(DirectoryFor(forum), HeadFile);
        if (!File.Exists(path)) return null;

        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }

        return JsonReader.Parse(bytes, ClientJson.Limits).TryGetValue(out var value, out _)
            && ActaDocuments.ReadHead(value!).TryGetValue(out var head, out _)
                ? head
                : null;
    }

    /// <summary>
    /// Retains a head whose signature the caller has already verified.
    ///
    /// <para>Only the signed object and the two members that identify its signature are kept. The
    /// Forum's own <c>signature_valid</c> and its <c>current_tree_size</c> are not: the first is the
    /// Forum re-verifying itself and is worthless once this client has done the check for real, and
    /// the second is a fact about the log at the moment of a fetch that has already passed.</para>
    /// </summary>
    public void Write(Uri forum, SignedHeadDocument head)
    {
        ArgumentNullException.ThrowIfNull(head);

        var directory = DirectoryFor(forum);
        PrivateFiles.CreateDirectory(directory);

        PrivateFiles.Write(Path.Combine(directory, HeadFile), ClientJson.Render(
        [
            new("head", head.Head),
            new("kid", new JsonValue.String(head.Kid)),
            new("log_index", new JsonValue.Number(head.LogIndex)),
            new("signature", new JsonValue.String(head.Signature)),
        ]));
    }
}
