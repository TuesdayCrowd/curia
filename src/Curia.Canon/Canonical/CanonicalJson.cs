using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using Curia.Canon.Envelope;
using Curia.Canon.Json;
using Curia.Domain.Primitives;

namespace Curia.Canon.Canonical;

/// <summary>
/// RFC 8785 (JSON Canonicalization Scheme) canonicalization.
///
/// <see cref="Canonicalize"/> is pure JCS: it normalizes nothing, ever. It is the
/// conformance target for the vendored RFC-author vectors under conformance/rfc8785/,
/// two of which ("unicode", "weird") specifically exist to prove JCS does not touch
/// Unicode normalization — an NFD string round-trips unchanged, and U+FB33 (on
/// Unicode's composition exclusion list) is not recomposed.
///
/// <see cref="CanonicalizeWithNfc"/> is the Cūria profile: RFC 8785 plus mandatory
/// Unicode NFC normalization of every object key and string value, applied as a step
/// inside the canonicalization function (R6.9) — never as a separate pass over stored
/// content, which §6.4's no-mutation invariant forbids. Signing and verification SHALL
/// use <see cref="CanonicalizeWithNfc"/>, never the bare <see cref="Canonicalize"/>.
///
/// These are deliberately two distinct, non-overloaded names rather than one function
/// with a flag: R6.9 is irreconcilable with bare RFC 8785 conformance on adversarial
/// input (proven by the two vendored vectors above), so a caller must not be able to
/// pick the wrong semantics by accident. <see cref="CanonicalizeWithNfc"/> normalizes
/// the tree and delegates to <see cref="Canonicalize"/> — one writer, two entry points —
/// so the two can never disagree byte-for-byte on already-NFC input except by a defect.
/// </summary>
public static class CanonicalJson
{
    /// <summary>Pinned per R6.34; changes only with an envelope schema version bump.</summary>
    public const string UnicodeVersion = "16.0";

    /// <summary>
    /// Pure RFC 8785. Normalizes nothing. See the type-level remarks.
    ///
    /// Warning: performs no Unicode normalization (R6.9). Do not call this on envelope
    /// content -- <see cref="EnvelopeDocument.Root"/> being public makes
    /// <c>Canonicalize(doc.Root)</c> a one-line way to reach this function instead of
    /// <see cref="CanonicalizeEnvelope"/>, silently skipping the NFC step signing and
    /// verification depend on. <see cref="CanonicalizeEnvelope"/> is the entry point for
    /// anything that will be signed or verified.
    ///
    /// Fallible for exactly two reasons, both of them well-definedness violations rather than
    /// any of ADMIT's policy caps, which R6.38's first paragraph forbids this function from
    /// re-enforcing (R6.38 ¶2, errata E2/E10/E13):
    ///
    /// <list type="bullet">
    /// <item><b>An object carrying two members with the same name</b>
    /// (<c>curia/admit/duplicate-key</c>). RFC 8785 defines no canonical output for that
    /// document -- §3.2.3 orders members by name and two equal names have no defined order, and
    /// JCS states duplicate-freedom as a precondition rather than a case its algorithm handles.
    /// See <see cref="OrderMembers"/> for where the check sits and why.</item>
    /// <item><b>A string carrying an unpaired UTF-16 surrogate</b>
    /// (<c>curia/admit/unpaired-surrogate</c>), in a member name or a value, at any depth. There
    /// is no canonical output for it either: an unpaired surrogate is not a Unicode scalar value
    /// and so has no UTF-8 encoding at all. See <see cref="WriteString"/>.</item>
    /// </list>
    ///
    /// In both cases emitting bytes for such a tree emits something the specification this
    /// function claims to implement does not define.
    ///
    /// This function is reachable with a tree no parse path ever inspected -- any caller
    /// holding a <see cref="JsonValue"/> it built itself, of which
    /// <c>Curia.Infrastructure.PostgresEventStore</c>'s payload serialization is the live
    /// example -- so it cannot assume <see cref="Json.JsonReader"/>'s own rejection of either
    /// condition has run. That assumption is precisely what errata E10 records as having
    /// silently held until a duplicate-membered event payload reached Postgres, whose
    /// <c>jsonb</c> resolves duplicates last-wins on the way in, and what errata E12 then found
    /// again for the surrogate half: a lone surrogate was written out literally here and became
    /// U+FFFD at the <c>Encoding.UTF8.GetBytes</c> step below, so this function returned
    /// <c>Ok</c> with bytes carrying a different character than the tree it was handed. Two
    /// conditions, one shape -- a check the byte parse path performs and the tree-taking entry
    /// point did not -- and one the differential harness cannot see, because it feeds bytes and
    /// the byte path was never wrong (R14.7).
    /// </summary>
    public static Result<CanonicalBytes> Canonicalize(JsonValue value)
    {
        var sb = new StringBuilder();
        if (Write(value, sb) is { } error)
            return Result<CanonicalBytes>.Fail(error);
        return Result<CanonicalBytes>.Ok(new CanonicalBytes(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    /// <summary>The Cūria profile (R6.9). See the type-level remarks.</summary>
    public static Result<CanonicalBytes> CanonicalizeWithNfc(JsonValue value)
    {
        var normalized = NormalizeToNfc(value);
        return normalized.TryGetValue(out var tree, out var error)
            ? Canonicalize(tree)
            : Result<CanonicalBytes>.Fail(error!);
    }

    /// <summary>
    /// The same tree with every object's members in RFC 8785 §3.2.3 order, at every depth.
    /// Deliberately *not* named "Canonicalize...": this is one step of canonicalization, not
    /// the whole of either profile, and the type-level remarks' insistence that a caller must
    /// not be able to pick the wrong semantics by typing a familiar prefix applies here too.
    ///
    /// What it does and does not touch is the whole of its contract. It reorders object
    /// members and nothing else: array order is preserved (R6.8), every scalar is the identical
    /// value it was, no string is normalized (that is <see cref="CanonicalizeWithNfc"/>'s job
    /// and §6.4 forbids performing it on stored content), and no number is re-laid-out (that
    /// happens in <see cref="JsonNumber.Serialize"/>, which only the writer reaches). It is
    /// therefore incapable of altering the document it is handed -- member order is not
    /// information in JSON (RFC 8259 §4: an object is an unordered collection) -- which is
    /// exactly why it can be applied to a payload already accepted into an append-only store
    /// without violating the no-mutation invariant.
    ///
    /// It shares <see cref="OrderMembers"/> with <see cref="Write"/>, so the order it produces
    /// and the order the writer emits cannot drift: errata E10's standing lesson is that one
    /// rule with two implementations is how the rule drifts, and §3.2.3's ordering is a rule
    /// that now has two consumers. It fails for the one reason the writer fails for -- an
    /// object carrying two members with the same name, which §3.2.3 gives no order for -- and
    /// reports the identical <c>curia/admit/duplicate-key</c> slug.
    ///
    /// It does not, on its own, decide that a tree *has* a canonical form: a number this
    /// function happily reorders around may still be one <see cref="Canonicalize"/> refuses.
    /// A caller wanting that verdict asks for it by canonicalizing; this function answers a
    /// narrower question and says so rather than implying the broader one. That is also why the
    /// unpaired-surrogate rejection R6.38 requires of the two canonicalizers (errata E13) is
    /// deliberately *not* mirrored here: the duplicate-name check is present because §3.2.3's
    /// sort is the step with nothing to do, so this function genuinely cannot answer, whereas
    /// reordering members around a string with an ill-formed surrogate is well defined and
    /// lossless -- no string is encoded, so nothing can be substituted. The event store, this
    /// function's live caller, is not exposed either way: R11.24 has both adapters admit under
    /// <see cref="CanonicalizeWithNfc"/>, which refuses such a payload before it is stored.
    ///
    /// Its live caller is the event store, whose port promises that a payload read back is in
    /// this order whichever adapter is underneath (R11.23): PostgreSQL's <c>jsonb</c> is a
    /// parsed binary form that re-sorts object keys by its own rule (key length, then
    /// bytewise), so the RFC 8785 order the adapter wrote is not the order it reads, and the
    /// in-memory adapter -- which physically *can* hand back the caller's exact tree -- would
    /// otherwise be the more faithful of the two, which misleads exactly as being the more
    /// permissive did (E11, R11.22).
    /// </summary>
    public static Result<JsonValue> InCanonicalMemberOrder(JsonValue value)
    {
        switch (value)
        {
            case JsonValue.Object o:
            {
                if (!OrderMembers(o).TryGetValue(out var ordered, out var orderError))
                    return Result<JsonValue>.Fail(orderError!);

                var members = ImmutableArray.CreateBuilder<KeyValuePair<string, JsonValue>>(ordered.Length);
                foreach (var member in ordered)
                {
                    if (!InCanonicalMemberOrder(member.Value).TryGetValue(out var orderedValue, out var valueError))
                        return Result<JsonValue>.Fail(valueError!);
                    members.Add(new KeyValuePair<string, JsonValue>(member.Key, orderedValue));
                }

                return Result<JsonValue>.Ok(new JsonValue.Object(members.MoveToImmutable()));
            }

            case JsonValue.Array a:
            {
                var items = ImmutableArray.CreateBuilder<JsonValue>(a.Items.Length);
                foreach (var item in a.Items)
                {
                    if (!InCanonicalMemberOrder(item).TryGetValue(out var orderedItem, out var itemError))
                        return Result<JsonValue>.Fail(itemError!);
                    items.Add(orderedItem);
                }

                return Result<JsonValue>.Ok(new JsonValue.Array(items.MoveToImmutable()));
            }

            case JsonValue.String s: return Result<JsonValue>.Ok(s);
            case JsonValue.Number n: return Result<JsonValue>.Ok(n);
            case JsonValue.Bool b:   return Result<JsonValue>.Ok(b);
            case JsonValue.Null n:   return Result<JsonValue>.Ok(n);
            default:
                // Unreachable for the same reason NormalizeToNfc's and Write's default arms
                // are: JsonValue is closed to this assembly (CS-11).
                throw new ArgumentOutOfRangeException(nameof(value), value, "Unhandled JsonValue case");
        }
    }

    /// <summary>
    /// Canonicalizes an <see cref="EnvelopeDocument"/> for signing, verification, and
    /// digesting. Always the Cūria profile (R6.9) — an envelope is exactly the signed
    /// content R6.9 governs — never the bare RFC 8785 <see cref="Canonicalize"/>. Named
    /// distinctly rather than added as a <c>Canonicalize(EnvelopeDocument)</c> overload:
    /// an overload sharing the "Canonicalize" name would let a caller reach the NFC
    /// profile by typing the same short name used for the pure-RFC-8785 function on a
    /// plain <see cref="JsonValue"/>, reintroducing by the back door exactly the
    /// wrong-semantics-by-accident hazard <see cref="CanonicalizeWithNfc"/>'s distinct
    /// name exists to prevent (see the type-level remarks).
    /// </summary>
    public static Result<CanonicalBytes> CanonicalizeEnvelope(EnvelopeDocument doc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        return CanonicalizeWithNfc(doc.Root);
    }

    /// <summary>
    /// Rebuilds the tree with every object key and string value NFC-normalized. A new
    /// tree rather than a mutation, because <see cref="JsonValue"/> is immutable and this
    /// runs on every canonicalize call rather than touching stored content (§6.4).
    ///
    /// Fallible for three reasons, all caught here because this is where the normalized
    /// tree is built -- the only place any of the three first exists to detect:
    ///
    /// <list type="bullet">
    /// <item>Normalizing an object's member names can make two distinct raw wire keys
    /// equal (e.g. precomposed "café" vs. "cafe" + combining acute, U+0301) --
    /// <see cref="NormalizeObject"/> rejects the collision rather than silently emitting
    /// a canonical object with two members sharing one key, which would not be valid
    /// I-JSON and would let two distinct wire documents share one canonical digest and
    /// signature (a non-repudiation defect).</item>
    /// <item>A string carrying an unpaired UTF-16 surrogate, which R6.38 requires both
    /// canonicalizers to reject independently of ADMIT. <see cref="NormalizeString"/> checks it
    /// before normalizing, so this profile reports the same <c>curia/admit/unpaired-surrogate</c>
    /// condition the pure writer and both parse paths do, instead of the
    /// <c>curia/canon/normalization-failed</c> the platform's own throw used to produce here
    /// (errata E12's finding, closed by E13).</item>
    /// <item><c>string.Normalize(NormalizationForm.FormC)</c> throws
    /// <see cref="ArgumentException"/> on some inputs .NET's ICU-backed implementation
    /// treats as invalid code points (observed for U+FFFE, a Unicode noncharacter read
    /// as a reversed byte-order mark). ADMIT rejects noncharacters before a
    /// <see cref="JsonValue"/> exists to reach this function on any real call path, but
    /// CS-10 requires domain fallibility to be a value even so -- see
    /// <see cref="NormalizeString"/>.</item>
    /// </list>
    /// </summary>
    private static Result<JsonValue> NormalizeToNfc(JsonValue value)
    {
        switch (value)
        {
            case JsonValue.Object o:
                return NormalizeObject(o);
            case JsonValue.Array a:
                return NormalizeArray(a);
            case JsonValue.String s:
                return NormalizeString(s.Value).Map(n => (JsonValue)new JsonValue.String(n));
            case JsonValue.Number n:
                return Result<JsonValue>.Ok(n);
            case JsonValue.Bool b:
                return Result<JsonValue>.Ok(b);
            case JsonValue.Null n:
                return Result<JsonValue>.Ok(n);
            default:
                // Unreachable: JsonValue is closed to this assembly (CS-11). A new case
                // added there without updating this switch fails loudly here rather than
                // silently dropping the case's content from a signed document.
                throw new ArgumentOutOfRangeException(nameof(value), value, "Unhandled JsonValue case");
        }
    }

    /// <summary>
    /// Normalizes one object's own member list. Two linear passes over the *same*
    /// member list, not one combined scan, so the outcome is independent of member
    /// order (mirrors curia-testis's <c>nfc.rs</c>, whose own fix history records why a
    /// single combined pass is wrong: it makes the reported slug depend on which
    /// collision the scan happens to reach first, and the corpus pins exact slugs).
    ///
    /// Pass 1 rejects a raw, byte-identical duplicate member name with the same
    /// <c>curia/admit/duplicate-key</c> predicate ADMIT itself uses -- this is the
    /// identical defect, just noticed by a caller that reached this function without
    /// ADMIT having run first, and a verifier should report the same slug for the same
    /// defect regardless of which layer noticed it. Pass 1 runs to completion, over
    /// every member, before pass 2 computes a single normalized name, which is what
    /// makes a raw duplicate always win over an NFC-created collision in the same
    /// object -- regardless of which pair appears earlier -- rather than whichever
    /// defect the scan happens to reach first.
    ///
    /// Pass 2 normalizes every remaining (by definition raw-unique) member name and
    /// value, rejecting with the distinct <c>curia/canon/duplicate-normalized-key</c>
    /// predicate when two raw-distinct names normalize to the same string. The check is
    /// scoped to this one object's member list, not the whole document: RFC 8785
    /// §3.2.3 ordering and duplicate-freedom are properties of one member list, so two
    /// equal normalized names in different objects (siblings or otherwise) are fine.
    /// </summary>
    private static Result<JsonValue> NormalizeObject(JsonValue.Object o)
    {
        var rawSeen = new HashSet<string>(o.Members.Length, StringComparer.Ordinal);
        foreach (var member in o.Members)
        {
            if (!rawSeen.Add(member.Key))
                return Result<JsonValue>.Fail(CanonErrors.DuplicateKey(member.Key));
        }

        var members = ImmutableArray.CreateBuilder<KeyValuePair<string, JsonValue>>(o.Members.Length);
        var normalizedSeen = new HashSet<string>(o.Members.Length, StringComparer.Ordinal);
        foreach (var member in o.Members)
        {
            var keyResult = NormalizeString(member.Key);
            if (!keyResult.TryGetValue(out var normalizedKey, out var keyError))
                return Result<JsonValue>.Fail(keyError!);

            if (!normalizedSeen.Add(normalizedKey))
                return Result<JsonValue>.Fail(CanonErrors.DuplicateNormalizedKey(normalizedKey));

            var valueResult = NormalizeToNfc(member.Value);
            if (!valueResult.TryGetValue(out var normalizedValue, out var valueError))
                return Result<JsonValue>.Fail(valueError!);

            members.Add(new KeyValuePair<string, JsonValue>(normalizedKey, normalizedValue));
        }

        return Result<JsonValue>.Ok(new JsonValue.Object(members.MoveToImmutable()));
    }

    /// <summary>Normalizes every element of an array; order is preserved (R6.8).</summary>
    private static Result<JsonValue> NormalizeArray(JsonValue.Array a)
    {
        var items = ImmutableArray.CreateBuilder<JsonValue>(a.Items.Length);
        foreach (var item in a.Items)
        {
            var itemResult = NormalizeToNfc(item);
            if (!itemResult.TryGetValue(out var normalizedItem, out var error))
                return Result<JsonValue>.Fail(error!);
            items.Add(normalizedItem);
        }

        return Result<JsonValue>.Ok(new JsonValue.Array(items.MoveToImmutable()));
    }

    /// <summary>
    /// NFC-normalizes one string, after rejecting an unpaired UTF-16 surrogate (R6.38 ¶2,
    /// errata E13) -- see the guard's own comment for why that check precedes everything else
    /// here rather than being left to the writer this profile eventually delegates to.
    ///
    /// R6.38 (errata E2) requires this to succeed on a Unicode
    /// noncharacter, not merely fail instead of crashing: "R6.38 requires a noncharacter to
    /// reach CanonicalizeWithNfc directly and be canonicalized, not rejected... a distinct
    /// defect from the accept/reject question R6.38 settles." <c>string.Normalize(NormalizationForm.FormC)</c>
    /// throws <see cref="ArgumentException"/> on this runtime for U+FFFE specifically (ICU
    /// reads it as a reversed byte-order mark) rather than performing the identity transform
    /// Unicode's own normalization-stability guarantee promises for it -- a noncharacter has
    /// no canonical decomposition and combining class 0, so it can never participate in, or
    /// be affected by, the composition of any character before or after it.
    ///
    /// The fast path below (no noncharacter present) is what every real signed envelope
    /// takes, because ADMIT already rejects noncharacters before wire content becomes a
    /// <see cref="JsonValue"/> on any call path reachable from real input; this function's
    /// contract does not get to assume its caller ran ADMIT first, though (R6.38 requires
    /// <see cref="CanonicalizeWithNfc"/> itself, not only ADMIT, to accept a noncharacter --
    /// e.g. when a caller reaches it via <see cref="Json.JsonReader.ParseUnrestricted"/> or
    /// builds a <see cref="JsonValue"/> tree directly), so the slow path exists precisely for
    /// that caller: it relies on the same combining-class-0 guarantee to split the string at
    /// each noncharacter, normalize the noncharacter-free runs independently (each of which
    /// normalizes exactly as it would as part of the whole string, since a noncharacter
    /// cannot be part of either run's combining sequence), and splice the noncharacters back
    /// in unchanged -- producing output byte-identical to what a platform whose normalizer
    /// does not choke on the input would produce in one pass.
    /// </summary>
    private static Result<string> NormalizeString(string s)
    {
        // Ahead of everything else on this path, including the noncharacter scan below, because
        // this is the condition and the alternatives are symptoms of it. string.Normalize(FormC)
        // throws ArgumentException ("String contains invalid Unicode code points") on ill-formed
        // UTF-16, so without this the NFC profile reported curia/canon/normalization-failed --
        // the layer that noticed rather than the condition, carrying a platform-specific ICU
        // message as its detail, which R6.43 now rules out generally and which neither
        // JsonReader nor curia-testis says for the identical input. EnumerateRunes, which the
        // noncharacter scan uses, would also have to substitute U+FFFD for each ill-formed
        // subsequence before it could report anything at all.
        if (HasUnpairedSurrogate(s))
            return Result<string>.Fail(CanonErrors.UnpairedSurrogate());

        if (!ContainsNoncharacter(s))
            return NormalizeRun(s);

        var sb = new StringBuilder(s.Length);
        var run = new StringBuilder();
        foreach (var rune in s.EnumerateRunes())
        {
            if (JsonReader.IsNoncharacter(rune.Value))
            {
                if (run.Length > 0)
                {
                    if (!NormalizeRun(run.ToString()).TryGetValue(out var normalizedRun, out var runError))
                        return Result<string>.Fail(runError!);
                    sb.Append(normalizedRun);
                    run.Clear();
                }

                sb.Append(rune);
            }
            else
            {
                run.Append(rune);
            }
        }

        if (run.Length > 0)
        {
            if (!NormalizeRun(run.ToString()).TryGetValue(out var lastRun, out var lastError))
                return Result<string>.Fail(lastError!);
            sb.Append(lastRun);
        }

        return Result<string>.Ok(sb.ToString());
    }

    private static bool ContainsNoncharacter(string s)
    {
        foreach (var rune in s.EnumerateRunes())
        {
            if (JsonReader.IsNoncharacter(rune.Value))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Normalizes one noncharacter-free run. Isolated from <see cref="NormalizeString"/> so
    /// the noncharacter-splitting slow path there can reuse the identical try/catch instead
    /// of duplicating it (CS-10: fallibility is a value here too, in the unlikely case a
    /// run-local defect the noncharacter split does not anticipate ever surfaces).
    /// </summary>
    private static Result<string> NormalizeRun(string run)
    {
        try
        {
            return Result<string>.Ok(run.Normalize(NormalizationForm.FormC));
        }
        catch (ArgumentException ex)
        {
            return Result<string>.Fail(CanonErrors.NormalizationFailed(ex.Message));
        }
    }

    /// <summary>
    /// RFC 8785 §3.2.3's member ordering, and the one place it is expressed. Returns this
    /// object's members sorted by name, or the duplicate-member-name failure the sort exposes.
    ///
    /// Factored out of <see cref="Write"/> when <see cref="InCanonicalMemberOrder"/> became a
    /// second consumer of the same rule, rather than sorted a second time there: errata E10's
    /// standing lesson is that one rule with two implementations is how the rule drifts, and a
    /// canonical order the writer emits but a reordering function disagrees with would be that
    /// drift in its most invisible form -- the bytes and the tree would each be
    /// self-consistent and would not describe the same document.
    ///
    /// The duplicate-member-name check (R6.38) sits here, immediately after the sort and
    /// before a single byte of the object is emitted, for three reasons:
    ///
    /// <list type="bullet">
    /// <item>It is exactly where the condition becomes undefined. §3.2.3 says to order
    /// members by name; two equal names are the one input for which that instruction picks
    /// no order, so the sort is the step with nothing to do rather than a step with a
    /// choice to make.</item>
    /// <item>It is linear in the member count and allocates nothing beyond the sorted list
    /// the caller needs anyway: sorting has already brought equal names adjacent
    /// (<see cref="Utf16Ordinal"/> compares ordinally, so names comparing equal are
    /// string-equal), making one pass over neighbours sufficient. Neither a per-object hash
    /// set nor -- far worse on the unbounded member lists <c>Curia.Domain</c>'s events carry,
    /// which have no member-count cap of their own -- a nested pairwise scan is needed.</item>
    /// <item>Checking the sorted list rather than the source list makes the reported key
    /// independent of wire member order, matching the order-independence errata E1 made
    /// normative for <see cref="NormalizeObject"/>'s own two duplicate predicates.</item>
    /// </list>
    ///
    /// <see cref="CanonicalizeWithNfc"/> can never reach this check: it normalizes the whole
    /// tree first, and <see cref="NormalizeObject"/> rejects both a raw duplicate
    /// (<c>curia/admit/duplicate-key</c>) and an NFC-created collision
    /// (<c>curia/canon/duplicate-normalized-key</c>) before delegating here, so every object
    /// this writer sees on that path already has pairwise-distinct names. The check is
    /// therefore additive for the NFC profile -- it cannot displace E1's slug precedence --
    /// and load-bearing only for callers of the bare <see cref="Canonicalize"/> and
    /// <see cref="InCanonicalMemberOrder"/>.
    /// </summary>
    private static Result<KeyValuePair<string, JsonValue>[]> OrderMembers(JsonValue.Object o)
    {
        var ordered = o.Members
            .OrderBy(m => m.Key, Utf16Ordinal.Comparer)
            .ToArray();

        for (var i = 1; i < ordered.Length; i++)
        {
            if (string.Equals(ordered[i - 1].Key, ordered[i].Key, StringComparison.Ordinal))
                return Result<KeyValuePair<string, JsonValue>[]>.Fail(CanonErrors.DuplicateKey(ordered[i].Key));
        }

        return Result<KeyValuePair<string, JsonValue>[]>.Ok(ordered);
    }

    /// <summary>
    /// The single RFC 8785 writer. Returns the first well-definedness failure it meets, or
    /// <c>null</c> on success -- an <see cref="Error"/> rather than a <c>Result&lt;T&gt;</c>
    /// only because this writer's success value is the <see cref="StringBuilder"/> it was
    /// handed; <see cref="Canonicalize"/> is where CS-10's <c>Result</c> is presented.
    /// Member ordering and the duplicate-member-name rejection that comes with it live in
    /// <see cref="OrderMembers"/>, which this writer shares with
    /// <see cref="InCanonicalMemberOrder"/>.
    /// </summary>
    private static Error? Write(JsonValue value, StringBuilder sb)
    {
        switch (value)
        {
            case JsonValue.Object o:
                if (!OrderMembers(o).TryGetValue(out var ordered, out var orderError))
                    return orderError;
                sb.Append('{');
                for (var i = 0; i < ordered.Length; i++)
                {
                    if (i > 0) sb.Append(',');
                    if (WriteString(ordered[i].Key, sb) is { } keyError)
                        return keyError;
                    sb.Append(':');
                    if (Write(ordered[i].Value, sb) is { } memberError)
                        return memberError;
                }
                sb.Append('}');
                return null;

            case JsonValue.Array a:
                sb.Append('[');
                for (var i = 0; i < a.Items.Length; i++)
                {
                    if (i > 0) sb.Append(',');
                    if (Write(a.Items[i], sb) is { } itemError)   // array order is preserved (R6.8)
                        return itemError;
                }
                sb.Append(']');
                return null;

            case JsonValue.String s: return WriteString(s.Value, sb);
            case JsonValue.Number n: sb.Append(JsonNumber.Serialize(n.Value)); return null;
            case JsonValue.Bool b:   sb.Append(b.Value ? "true" : "false"); return null;
            case JsonValue.Null:     sb.Append("null"); return null;
            default:
                // Unreachable for the same reason NormalizeToNfc's default arm is: JsonValue
                // is closed to this assembly (CS-11). Failing loudly here beats emitting a
                // document with a new case's content silently missing from it.
                throw new ArgumentOutOfRangeException(nameof(value), value, "Unhandled JsonValue case");
        }
    }

    /// <summary>
    /// True when <paramref name="s"/> carries a UTF-16 code unit that is not part of a
    /// well-formed surrogate pair: a high surrogate not immediately followed by a low one, or a
    /// low surrogate with no high one immediately before it. The one place this project's
    /// production code answers that question, for the reason errata E10 gives about one rule
    /// with two implementations -- <see cref="WriteString"/> and <see cref="NormalizeString"/>
    /// both need it, and <c>Curia.Canon.Tests</c>'s property generators reuse it (via
    /// <c>InternalsVisibleTo</c>) rather than keeping the private copy they used to carry, the
    /// same discipline that already applies to <see cref="JsonReader.IsNoncharacter"/>.
    ///
    /// Linear in the string's length and allocation-free. Deliberately *not* expressed as
    /// "contains any surrogate": every character outside the BMP is spelled in UTF-16 as a
    /// surrogate pair, so a check at that granularity would reject the whole of plane 1 upward,
    /// including the U+1F602 the RFC author's own <c>rfc8785/input-weird.json</c> uses as a
    /// member name.
    /// </summary>
    internal static bool HasUnpairedSurrogate(string s)
    {
        for (var i = 0; i < s.Length; i++)
        {
            if (char.IsHighSurrogate(s[i]))
            {
                if (i + 1 == s.Length || !char.IsLowSurrogate(s[i + 1]))
                    return true;
                i++;   // the low half is legitimately paired; do not re-examine it on its own
            }
            else if (char.IsLowSurrogate(s[i]))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// RFC 8785 §3.2.2.2 string escaping: minimal, with control characters escaped. The single
    /// place a string becomes canonical output, reached for object member names
    /// (<see cref="Write"/>'s object arm) and string values alike -- which is what makes the
    /// unpaired-surrogate rejection below cover both positions by construction rather than by
    /// two call sites each remembering to check.
    ///
    /// R6.38's second paragraph (errata E2, closed for this condition by E13) requires
    /// <see cref="Canonicalize"/> to reject an unpaired UTF-16 surrogate "independently of ADMIT
    /// and regardless of whether ADMIT already ran," for the same reason it rejects a raw
    /// duplicate member name: RFC 8785 defines no canonical output for it. The failure being
    /// prevented is silent substitution rather than a crash — a lone surrogate passed through
    /// this writer untouched and became U+FFFD at <see cref="Canonicalize"/>'s
    /// <c>Encoding.UTF8.GetBytes</c> step, so the function returned <c>Ok</c> with canonical
    /// bytes carrying a different character than the tree it was handed, and a digest over a
    /// document nobody wrote.
    ///
    /// Checked before a single character of the string is emitted, mirroring
    /// <see cref="OrderMembers"/>'s reasoning: on the failing path nothing has been written that
    /// a reader of the buffer could mistake for output. The slug is
    /// <c>curia/admit/unpaired-surrogate</c> — the condition, not the layer that noticed it
    /// (R6.43, generalizing R6.42 for the reason R6.40 gives) — which is what
    /// <see cref="Json.JsonReader"/>'s byte path and <c>curia-testis</c>'s <c>json::parse</c>
    /// already answer for the identical input.
    /// </summary>
    private static Error? WriteString(string s, StringBuilder sb)
    {
        if (HasUnpairedSurrogate(s))
            return CanonErrors.UnpairedSurrogate();

        sb.Append('"');
        foreach (var c in s)
        {
            switch (c)
            {
                case '"':  sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20) sb.Append(CultureInfo.InvariantCulture, $"\\u{(int)c:x4}");
                    else sb.Append(c);           // everything else literal UTF-8
                    break;
            }
        }
        sb.Append('"');
        return null;
    }
}
