using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace Curia.Architecture.Tests;

/// <summary>
/// R11.34 (errata G18): reads a shipped assembly's IL metadata with
/// <see cref="System.Reflection.Metadata"/>, for the fences that need what a host actually references
/// rather than what a type declares.
/// </summary>
internal static class IlCallSites
{
    /// <summary>
    /// Every method or constructor that <paramref name="hostAssemblyPath"/> references in one of
    /// <paramref name="targetAssemblies"/>, read from <see cref="MetadataReader.MemberReferences"/>. A
    /// reference is kept when its parent, walked through nested <see cref="TypeReference"/>s to the
    /// outermost type and through a <see cref="TypeSpecification"/> to its generic type definition, has
    /// a <see cref="TypeReference.ResolutionScope"/> that is an <see cref="AssemblyReference"/> named in
    /// <paramref name="targetAssemblies"/>. <c>TypeFullName</c> is the parent's own name in reflection's
    /// form (<c>Namespace.Outer+Inner</c>, a generic definition with its arity), so
    /// <see cref="System.Reflection.Assembly.GetType(string)"/> finds it.
    /// </summary>
    internal static ImmutableArray<(string TypeFullName, string MemberName)> MemberReferencesInto(
        string hostAssemblyPath, params string[] targetAssemblies)
    {
        var targets = new HashSet<string>(targetAssemblies, StringComparer.Ordinal);
        using var stream = File.OpenRead(hostAssemblyPath);
        using var pe = new PEReader(stream);
        var md = pe.GetMetadataReader();

        var found = new HashSet<(string, string)>();
        foreach (var handle in md.MemberReferences)
        {
            var reference = md.GetMemberReference(handle);
            if (reference.GetKind() != MemberReferenceKind.Method)
                continue;

            if (TypeReferenceOf(md, reference.Parent) is not { } typeHandle)
                continue;

            if (AssemblyOf(md, typeHandle) is not { } assembly || !targets.Contains(assembly))
                continue;

            found.Add((FullNameOf(md, typeHandle), md.GetString(reference.Name)));
        }

        return [.. found.OrderBy(p => p.Item1, StringComparer.Ordinal).ThenBy(p => p.Item2, StringComparer.Ordinal)];
    }

    /// <summary>The parent as a type reference: itself, or a generic instantiation's definition.</summary>
    private static TypeReferenceHandle? TypeReferenceOf(MetadataReader md, EntityHandle parent)
    {
        if (parent.Kind == HandleKind.TypeReference)
            return (TypeReferenceHandle)parent;

        if (parent.Kind != HandleKind.TypeSpecification)
            return null;

        var blob = md.GetBlobReader(md.GetTypeSpecification((TypeSpecificationHandle)parent).Signature);
        if (blob.ReadSignatureTypeCode() != SignatureTypeCode.GenericTypeInstance)
            return null;

        // ECMA-335 II.23.2.12: GENERICINST (CLASS | VALUETYPE) TypeDefOrRefOrSpecEncoded ...
        blob.ReadCompressedInteger();
        var definition = blob.ReadTypeHandle();
        return definition.Kind == HandleKind.TypeReference ? (TypeReferenceHandle)definition : null;
    }

    /// <summary>The assembly the outermost enclosing type reference resolves to, if any.</summary>
    private static string? AssemblyOf(MetadataReader md, TypeReferenceHandle handle)
    {
        var scope = md.GetTypeReference(handle).ResolutionScope;
        while (scope.Kind == HandleKind.TypeReference)
            scope = md.GetTypeReference((TypeReferenceHandle)scope).ResolutionScope;

        return scope.Kind == HandleKind.AssemblyReference
            ? md.GetString(md.GetAssemblyReference((AssemblyReferenceHandle)scope).Name)
            : null;
    }

    private static string FullNameOf(MetadataReader md, TypeReferenceHandle handle)
    {
        var type = md.GetTypeReference(handle);
        var name = md.GetString(type.Name);
        if (type.ResolutionScope.Kind == HandleKind.TypeReference)
            return FullNameOf(md, (TypeReferenceHandle)type.ResolutionScope) + "+" + name;

        var ns = md.GetString(type.Namespace);
        return ns.Length == 0 ? name : ns + "." + name;
    }
}
