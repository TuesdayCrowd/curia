using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Curia.Canon.Canonical;
using Curia.Canon.Json;
using Microsoft.AspNetCore.Http;

namespace Curia.Api.Tests.Fuzz;

/// <summary>The varied envelope has no canonical form, so it has no re-signed copy (spec §4.10).</summary>
[SuppressMessage("Design", "CA1032:Implement standard exception constructors", Justification = "Thrown with one message only, by SignPost; the pass catches it by type.")]
[SuppressMessage("Performance", "CA1812:Avoid uninstantiated internal classes", Justification = "SignPost throws it; falsification case F18 removes that throw and must still build, so its fact goes red rather than the build.")]
internal sealed class NotReSignableException() : Exception("the varied envelope has no canonical form, so it has no re-signed copy (spec §4.10)");

/// <summary>A header's parameter, such as a Content-Type's <c>charset</c>.</summary>
internal sealed record HeaderParameter(string Name, string Value);

/// <summary>
/// A header: a value and its parameters, or a JWS the model holds, written after a prefix. A
/// header's parameters are its children, each a part of its own.
/// </summary>
internal sealed record HeaderEntry(string Name, string Value, IReadOnlyList<HeaderParameter> Parameters, string? Jws = null)
{
    internal string Text(string? compact) =>
        Jws is not null
            ? Value + compact
            : Value + string.Concat(Parameters.Select(p => "; " + p.Name + "=" + p.Value));
}

/// <summary>A form field: a value, or a JWS the model holds.</summary>
internal sealed record FormField(string Name, string Value, string? Jws = null);

/// <summary>A JWS the model holds: its header and claims as trees, and the key it is signed with. A detached post signature has no claims.</summary>
internal sealed record JwsEntry(JsonObject Header, JsonObject? Claims, ECDsa Signer);

/// <summary>A request's body.</summary>
internal abstract record RequestBody;

/// <summary>A JSON document. A submission names the member its signature covers and the member the signature is written at.</summary>
internal sealed record JsonBody(JsonNode Root, string? SignedPointer = null, string? SignaturePointer = null) : RequestBody;

/// <summary>A form, urlencoded or multipart; either declares <c>charset=utf-8</c>, on the request or on each part.</summary>
internal sealed record FormBody(IReadOnlyList<FormField> Fields, bool Multipart) : RequestBody
{
    internal const string Boundary = "curia-fuzz-boundary";
}

/// <summary>
/// One request as a tree of parts (spec §4.10). <see cref="Parts"/> walks it; nothing is listed by
/// hand. <see cref="Render"/> writes it with at most one part varied, signs every JWS over what it
/// will carry, and derives what a proof binds -- <c>htu</c> from the path the host will read, and
/// <c>ath</c> from the token sent -- unless that claim is the part being varied.
/// </summary>
internal sealed class RequestModel(string method, string pattern)
{
    internal const string Origin = "http://localhost";

    internal string Method { get; } = method;

    internal string Pattern { get; } = pattern;

    internal List<KeyValuePair<string, string>> RouteValues { get; } = [];

    internal List<KeyValuePair<string, string>> Query { get; } = [];

    internal List<HeaderEntry> Headers { get; } = [];

    internal RequestBody? Body { get; set; }

    /// <summary>JWS by name, in the order they are signed: a proof's <c>ath</c> needs the token first.</summary>
    internal Dictionary<string, JwsEntry> Jws { get; } = new(StringComparer.Ordinal);

    /// <summary>The addresses regenerated on every send.</summary>
    internal HashSet<string> Fresh { get; } = new(StringComparer.Ordinal);

    /// <summary>Published caps by address.</summary>
    internal Dictionary<string, int> Caps { get; } = new(StringComparer.Ordinal);

    private static readonly string[] JwsOrder = ["token", "assertion", "post", "proof"];

    private IEnumerable<string> JwsNames => JwsOrder.Where(Jws.ContainsKey).Concat(Jws.Keys.Where(k => !JwsOrder.Contains(k)));

    internal IReadOnlyList<Part> Parts()
    {
        var parts = new List<Part>();
        Part Make(string address, PartKind kind, PartValueKind valueKind) =>
            new(address, kind, valueKind, Fresh.Contains(address), Caps.TryGetValue(address, out var cap) ? cap : null);

        foreach (var (name, _) in RouteValues) parts.Add(Make("path:" + name, PartKind.Path, PartValueKind.String));
        foreach (var (name, _) in Query) parts.Add(Make("query:" + name, PartKind.Query, PartValueKind.String));
        foreach (var header in Headers)
        {
            parts.Add(Make("header:" + header.Name, PartKind.Header, PartValueKind.String));
            foreach (var parameter in header.Parameters)
                parts.Add(Make("header:" + header.Name + ";" + parameter.Name, PartKind.HeaderParameter, PartValueKind.String));
        }

        switch (Body)
        {
            case FormBody form:
                parts.Add(Make("form:", PartKind.Form, PartValueKind.Object));
                foreach (var field in form.Fields) parts.Add(Make("form:" + field.Name, PartKind.Form, PartValueKind.String));
                break;
            case JsonBody json:
                foreach (var (pointer, node) in RawJson.Walk(json.Root))
                    parts.Add(Make("json:" + pointer, PartKind.Json, RawJson.KindOf(node)));
                break;
            default:
                break;
        }

        foreach (var name in JwsNames)
        {
            var entry = Jws[name];
            foreach (var (pointer, node) in RawJson.Walk(entry.Header))
                parts.Add(Make($"jws:{name}:header{pointer}", PartKind.Jws, RawJson.KindOf(node)));
            if (entry.Claims is null) continue;
            foreach (var (pointer, node) in RawJson.Walk(entry.Claims))
                parts.Add(Make($"jws:{name}:claims{pointer}", PartKind.Jws, RawJson.KindOf(node)));
        }

        return parts;
    }

    /// <summary>Whether a variation of <paramref name="part"/> is sent re-signed and unsigned rather than plain.</summary>
    internal bool IsSigned(Part part)
    {
        ArgumentNullException.ThrowIfNull(part);
        if (part.Kind == PartKind.Jws) return true;
        if (part.Kind != PartKind.Json || Body is not JsonBody { SignedPointer: { } signed }) return false;
        var pointer = part.Address["json:".Length..];
        return string.Equals(pointer, signed, StringComparison.Ordinal) || pointer.StartsWith(signed + "/", StringComparison.Ordinal);
    }

    /// <summary>The exemplar's value at a part, as text, after what the model derives has been derived.</summary>
    internal string? ValueOf(Part part)
    {
        ArgumentNullException.ThrowIfNull(part);
        var rendered = Prepare(null, null, CopyKind.Plain);
        var address = part.Address;

        if (address.StartsWith("path:", StringComparison.Ordinal))
            return RouteValues.First(kv => kv.Key == address["path:".Length..]).Value;
        if (address.StartsWith("query:", StringComparison.Ordinal))
            return Query.First(kv => kv.Key == address["query:".Length..]).Value;
        if (address.StartsWith("header:", StringComparison.Ordinal))
        {
            var spec = address["header:".Length..];
            var split = spec.IndexOf(';', StringComparison.Ordinal);
            var header = Headers.First(h => h.Name == (split < 0 ? spec : spec[..split]));
            return split < 0
                ? header.Text(header.Jws is null ? null : rendered.Compacts[header.Jws])
                : header.Parameters.First(p => p.Name == spec[(split + 1)..]).Value;
        }

        if (address == "form:" || address == "json:")
            return Encoding.UTF8.GetString(rendered.Body ?? []);
        if (address.StartsWith("form:", StringComparison.Ordinal))
        {
            var field = ((FormBody)Body!).Fields.First(f => f.Name == address["form:".Length..]);
            return field.Jws is null ? field.Value : rendered.Compacts[field.Jws];
        }

        if (address.StartsWith("json:", StringComparison.Ordinal))
            return RawJson.TextOf(RawJson.At(((JsonBody)Body!).Root, address["json:".Length..]));

        var (name, claims, pointer) = JwsTarget(address)!.Value;
        var entry = Jws[name];
        return RawJson.TextOf(RawJson.At(claims ? entry.Claims : entry.Header, pointer));
    }

    /// <summary>
    /// The request, with <paramref name="value"/> at <paramref name="part"/>. Throws, before anything
    /// is sent, when no request can be built: a path holding U+0000 has no path the host can read,
    /// so no <c>htu</c> can be bound to it, and a header the client will not carry is refused here.
    /// </summary>
    internal HttpRequestMessage Render(Part? part, VariedValue? value, CopyKind copy)
    {
        var rendered = Prepare(part, value, copy);
        var request = new HttpRequestMessage(new HttpMethod(Method), new Uri(rendered.Url, UriKind.Relative));
        try
        {
            HttpContent? content = rendered.Body is null ? null : new ByteArrayContent(rendered.Body);
            var bodyVaried = part is { IsBodyRoot: true } && value is VariedValue.Body;

            foreach (var header in Headers)
            {
                var text = HeaderText(header, part, value, rendered);
                if (text is null) continue;
                var isContent = header.Name.StartsWith("Content-", StringComparison.OrdinalIgnoreCase);
                if (isContent)
                {
                    if (content is null || (bodyVaried && ((VariedValue.Body)value!).Bytes.Length == 0)) continue;
                    if (!content.Headers.TryAddWithoutValidation(header.Name, text))
                        throw new InvalidOperationException($"the client will not carry {header.Name}");
                }
                else if (!request.Headers.TryAddWithoutValidation(header.Name, text))
                {
                    throw new InvalidOperationException($"the client will not carry {header.Name}");
                }
            }

            if (bodyVaried && ((VariedValue.Body)value!).Bytes.Length == 0)
            {
                content?.Dispose();
                content = null;
            }

            request.Content = content;
            return request;
        }
        catch
        {
            request.Dispose();
            throw;
        }
    }

    private static string? HeaderText(HeaderEntry header, Part? part, VariedValue? value, Rendered rendered)
    {
        var address = "header:" + header.Name;
        if (part is not null && string.Equals(part.Address, address, StringComparison.Ordinal))
            return value is VariedValue.HeaderText t ? t.Text : null;

        if (part is not null && part.Kind == PartKind.HeaderParameter && part.Address.StartsWith(address + ";", StringComparison.Ordinal))
        {
            var name = part.Address[(address.Length + 1)..];
            var parameters = header.Parameters
                .Where(p => p.Name != name || value is not VariedValue.Removed)
                .Select(p => p.Name == name ? p with { Value = ((VariedValue.HeaderText)value!).Text } : p)
                .ToList();
            return (header with { Parameters = parameters }).Text(null);
        }

        return header.Text(header.Jws is null ? null : rendered.Compacts[header.Jws]);
    }

    private sealed record Rendered(string Url, Dictionary<string, string> Compacts, byte[]? Body);

    /// <summary>The URL, every JWS compact, and the body's bytes, with the variation applied.</summary>
    private Rendered Prepare(Part? part, VariedValue? value, CopyKind copy)
    {
        var address = part?.Address;

        // The path, and the path the host will read: the proof's htu is bound to that.
        var path = Pattern;
        foreach (var (name, plain) in RouteValues)
        {
            var text = string.Equals(address, "path:" + name, StringComparison.Ordinal)
                ? value switch { VariedValue.Encoded e => e.Text, _ => string.Empty }
                : Uri.EscapeDataString(plain);
            path = ReplaceParameter(path, name, text);
        }

        var query = new List<string>();
        foreach (var (name, plain) in Query)
        {
            if (string.Equals(address, "query:" + name, StringComparison.Ordinal))
            {
                if (value is VariedValue.Encoded e) query.Add(name + "=" + e.Text);
                continue;
            }

            query.Add(name + "=" + Uri.EscapeDataString(plain));
        }

        var url = query.Count == 0 ? path : path + "?" + string.Join('&', query);
        var htu = Origin + PathString.FromUriComponent(new Uri(Origin + path)).ToUriComponent();

        var compacts = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in JwsNames)
        {
            var entry = Jws[name];
            if (name == "proof" && entry.Claims is { } proofClaims)
            {
                if (proofClaims.ContainsKey("htu")) proofClaims["htu"] = htu;
                if (proofClaims.ContainsKey("ath") && compacts.TryGetValue("token", out var token)) proofClaims["ath"] = JwsBuilder.Ath(token);
            }

            compacts[name] = name == "post" ? SignPost(entry, address, value, copy) : SignCompact(name, entry, address, value, copy);
        }

        byte[]? body = null;
        switch (Body)
        {
            case JsonBody json:
                if (json.SignaturePointer is { } signature && compacts.TryGetValue("post", out var post))
                    SetAt(json.Root, signature, post);
                body = address is not null && address.StartsWith("json:", StringComparison.Ordinal)
                    ? RawJson.Write(json.Root, address["json:".Length..], value)
                    : RawJson.Write(json.Root, null, null);
                break;
            case FormBody form:
                body = FormBytes(form, address, value, compacts);
                break;
            default:
                break;
        }

        if (part is { IsBodyRoot: true } && value is VariedValue.Body replaced)
            body = replaced.Bytes.Length == 0 ? [] : replaced.Bytes;

        return new Rendered(url, compacts, body);
    }

    private static string ReplaceParameter(string path, string name, string text)
    {
        var start = path.IndexOf("{" + name, StringComparison.Ordinal);
        var end = path.IndexOf('}', start);
        return string.Concat(path.AsSpan(0, start), text, path.AsSpan(end + 1));
    }

    private static void SetAt(JsonNode root, string pointer, string text)
    {
        var parent = pointer[..pointer.LastIndexOf('/')];
        var key = pointer[(pointer.LastIndexOf('/') + 1)..];
        ((JsonObject)RawJson.At(root, parent)!)[key] = text;
    }

    /// <summary>The JWS a part addresses: its name, whether the claims, and the pointer inside.</summary>
    private static (string Name, bool Claims, string Pointer)? JwsTarget(string? address)
    {
        if (address is null || !address.StartsWith("jws:", StringComparison.Ordinal)) return null;
        var rest = address["jws:".Length..];
        var colon = rest.IndexOf(':', StringComparison.Ordinal);
        var name = rest[..colon];
        var tail = rest[(colon + 1)..];
        return tail.StartsWith("claims", StringComparison.Ordinal)
            ? (name, true, tail["claims".Length..])
            : (name, false, tail["header".Length..]);
    }

    private static string SignCompact(string name, JwsEntry entry, string? address, VariedValue? value, CopyKind copy)
    {
        var header = RawJson.Write(entry.Header, null, null);
        var claims = RawJson.Write(entry.Claims, null, null);
        var original = JwsBuilder.Compact(header, claims, entry.Signer);
        if (JwsTarget(address) is not { } target || target.Name != name) return original;

        var variedHeader = target.Claims ? header : RawJson.Write(entry.Header, target.Pointer, value);
        var variedClaims = target.Claims ? RawJson.Write(entry.Claims, target.Pointer, value) : claims;
        return copy == CopyKind.ReSigned
            ? JwsBuilder.Compact(variedHeader, variedClaims, entry.Signer)
            : JwsBuilder.WithSignatureOf(variedHeader, variedClaims, original);
    }

    /// <summary>
    /// The detached post signature over the canonical form of the envelope as sent. An envelope that
    /// cannot be canonicalized has no re-signed copy: rendering one throws NotReSignableException, and
    /// the pass counts it superseded by the unre-signed copy, which is sent and counted.
    /// </summary>
    private string SignPost(JwsEntry entry, string? address, VariedValue? value, CopyKind copy)
    {
        var json = (JsonBody)Body!;
        var signed = json.SignedPointer!;
        var envelope = RawJson.At(json.Root, signed);
        var header = RawJson.Write(entry.Header, null, null);
        var original = JwsBuilder.Detached(header, Canonical(RawJson.Write(envelope, null, null))!, entry.Signer);

        if (JwsTarget(address) is { Name: "post" } target)
        {
            var variedHeader = RawJson.Write(entry.Header, target.Pointer, value);
            return copy == CopyKind.ReSigned
                ? JwsBuilder.Detached(variedHeader, Canonical(RawJson.Write(envelope, null, null))!, entry.Signer)
                : JwsBuilder.DetachedWithSignatureOf(variedHeader, original);
        }

        var pointer = address is not null && address.StartsWith("json:", StringComparison.Ordinal) ? address["json:".Length..] : null;
        var inside = pointer is not null && (pointer == signed || pointer.StartsWith(signed + "/", StringComparison.Ordinal));
        if (!inside || copy != CopyKind.ReSigned) return original;

        var relative = pointer![signed.Length..];
        var variedEnvelope = RawJson.Write(envelope, relative, value);
        return Canonical(variedEnvelope) is { } canonical ? JwsBuilder.Detached(header, canonical, entry.Signer) : throw new NotReSignableException();
    }

    /// <summary>The Cūria canonical form (JCS with NFC) of bytes, or null when they have none.</summary>
    private static byte[]? Canonical(byte[] json)
    {
        if (json.Length == 0) return null;
        return JsonReader.ParseUnrestricted(json).Bind(CanonicalJson.CanonicalizeWithNfc)
            .Match<byte[]?>(bytes => bytes.ToArray(), _ => null);
    }

    private static byte[] FormBytes(FormBody form, string? address, VariedValue? value, Dictionary<string, string> compacts)
    {
        var fields = new List<(string Name, byte[] Bytes, string Encoded)>();
        foreach (var field in form.Fields)
        {
            var plain = field.Jws is null ? field.Value : compacts[field.Jws];
            if (string.Equals(address, "form:" + field.Name, StringComparison.Ordinal))
            {
                if (value is VariedValue.Encoded e) fields.Add((field.Name, PercentDecode(e.Text), e.Text));
                continue;
            }

            fields.Add((field.Name, Encoding.UTF8.GetBytes(plain), Uri.EscapeDataString(plain)));
        }

        if (string.Equals(address, "form:", StringComparison.Ordinal) && value is VariedValue.Removed) return [];

        if (!form.Multipart)
            return Encoding.ASCII.GetBytes(string.Join('&', fields.Select(f => f.Name + "=" + f.Encoded)));

        using var stream = new MemoryStream();
        foreach (var (name, bytes, _) in fields)
        {
            stream.Write(Encoding.ASCII.GetBytes(
                $"--{FormBody.Boundary}\r\nContent-Disposition: form-data; name=\"{name}\"\r\nContent-Type: text/plain; charset=utf-8\r\n\r\n"));
            stream.Write(bytes);
            stream.Write("\r\n"u8);
        }

        stream.Write(Encoding.ASCII.GetBytes($"--{FormBody.Boundary}--\r\n"));
        return stream.ToArray();
    }

    /// <summary>The bytes percent-encoded text stands for: what a multipart part carries where a urlencoded form carries the text.</summary>
    private static byte[] PercentDecode(string text)
    {
        var bytes = new List<byte>(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '%' && i + 2 < text.Length)
            {
                bytes.Add(Convert.ToByte(text.Substring(i + 1, 2), 16));
                i += 2;
            }
            else
            {
                bytes.AddRange(Encoding.UTF8.GetBytes(text[i].ToString()));
            }
        }

        return [.. bytes];
    }
}
