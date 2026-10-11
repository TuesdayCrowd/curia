using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
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

    /// <summary>
    /// Every <c>call</c>, <c>callvirt</c>, <c>newobj</c>, <c>ldftn</c> and <c>ldvirtftn</c> in
    /// <paramref name="assemblyPath"/>'s method bodies (spec section 4.9). A delegate over a method is
    /// made with <c>ldftn</c>/<c>ldvirtftn</c> and is invoked through the delegate's <c>Invoke</c>, so a
    /// scan of calls alone never sees the method a method group names (R10.63's precedent,
    /// ConstantArgumentTests). Each body's IL is decoded with <see cref="BlobReader"/> and
    /// <see cref="ILOpCode"/>: the 4-byte token operand of those five opcodes is resolved, through a
    /// <see cref="MethodSpecification"/> to its generic method definition, to the callee's declaring
    /// type and name; every other operand is skipped by its encoded size, the <c>switch</c> table
    /// included. A body whose decoding does not end exactly at the body's length throws, because a
    /// decoder that lost its place would read nothing and pass. <c>CallerType</c> is the outermost
    /// declaring type, so a compiler-generated nested type (a lambda's closure, an async state machine)
    /// maps to the type that wrote it, and <c>CallerMethod</c> is the method the source wrote: the name
    /// inside a generated method's or state machine's angle brackets, or the method's own name.
    /// A <c>constrained. T</c> prefix before <c>callvirt</c> on a System.Object method is reported as
    /// T's method. Roslyn compiles <c>e.ToString()</c> on a struct whose override is declared in another
    /// assembly as <c>constrained. T callvirt Object::ToString</c>, so without this a ban on
    /// <c>JsonElement::ToString</c> could match nothing. A boxed or generic stringification names a BCL
    /// callee and is out of reach.
    /// </summary>
    internal static ImmutableArray<(string CallerType, string CallerMethod, string CalleeType, string CalleeMember)> CallSites(
        string assemblyPath)
    {
        using var stream = File.OpenRead(assemblyPath);
        using var pe = new PEReader(stream);
        var md = pe.GetMetadataReader();

        var found = new HashSet<(string, string, string, string)>();
        foreach (var typeHandle in md.TypeDefinitions)
        {
            var type = md.GetTypeDefinition(typeHandle);
            var callerType = DefinitionFullName(md, Outermost(md, typeHandle));
            foreach (var methodHandle in type.GetMethods())
            {
                var method = md.GetMethodDefinition(methodHandle);
                if (method.RelativeVirtualAddress == 0)
                    continue;

                var callerMethod = SourceMethodName(md, type, md.GetString(method.Name));
                var reader = pe.GetMethodBody(method.RelativeVirtualAddress).GetILReader();
                string? constrained = null;
                while (reader.Offset < reader.Length)
                {
                    var opCode = ReadOpCode(ref reader);
                    if (opCode == ILOpCode.Constrained)
                    {
                        var name = ParentFullName(md, MetadataTokens.EntityHandle(reader.ReadInt32()));
                        constrained = name.StartsWith('(') ? null : name;
                        continue;
                    }

                    if (opCode is ILOpCode.Call or ILOpCode.Callvirt or ILOpCode.Newobj or ILOpCode.Ldftn or ILOpCode.Ldvirtftn)
                    {
                        var (calleeType, calleeMember) = Callee(md, MetadataTokens.EntityHandle(reader.ReadInt32()));
                        if (opCode == ILOpCode.Callvirt && constrained is not null && calleeType == "System.Object")
                            calleeType = constrained;
                        found.Add((callerType, callerMethod, calleeType, calleeMember));
                        constrained = null;
                        continue;
                    }

                    constrained = null;
                    SkipOperand(ref reader, opCode);
                }

                if (reader.Offset != reader.Length)
                    throw new InvalidOperationException(
                        $"{callerType}.{md.GetString(method.Name)}: IL decoding ended at {reader.Offset} of {reader.Length}; the decoder lost its place");
            }
        }

        return [.. found
            .Select(f => (CallerType: f.Item1, CallerMethod: f.Item2, CalleeType: f.Item3, CalleeMember: f.Item4))
            .OrderBy(f => f.CallerType, StringComparer.Ordinal)
            .ThenBy(f => f.CallerMethod, StringComparer.Ordinal)
            .ThenBy(f => f.CalleeType, StringComparer.Ordinal)
            .ThenBy(f => f.CalleeMember, StringComparer.Ordinal)];
    }

    /// <summary>Each opcode's operand type, read once from <see cref="System.Reflection.Emit.OpCodes"/>.</summary>
    private static readonly Dictionary<ILOpCode, System.Reflection.Emit.OperandType> OperandTypes =
        typeof(System.Reflection.Emit.OpCodes)
            .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Select(f => (System.Reflection.Emit.OpCode)f.GetValue(null)!)
            .ToDictionary(o => (ILOpCode)(ushort)o.Value, o => o.OperandType);

    /// <summary>One opcode: a single byte, or <c>0xFE</c> and a second byte (ECMA-335 III.1.2.1).</summary>
    private static ILOpCode ReadOpCode(ref BlobReader reader)
    {
        var first = reader.ReadByte();
        return first == 0xFE ? (ILOpCode)(0xFE00 | reader.ReadByte()) : (ILOpCode)first;
    }

    private static void SkipOperand(ref BlobReader reader, ILOpCode opCode)
    {
        if (!OperandTypes.TryGetValue(opCode, out var operand))
            throw new InvalidOperationException($"IL opcode 0x{(int)opCode:X4} is not one System.Reflection.Emit.OpCodes names; the decoder lost its place");

        switch (operand)
        {
            case System.Reflection.Emit.OperandType.InlineNone:
                break;
            case System.Reflection.Emit.OperandType.ShortInlineBrTarget:
            case System.Reflection.Emit.OperandType.ShortInlineI:
            case System.Reflection.Emit.OperandType.ShortInlineVar:
                reader.Offset += 1;
                break;
            case System.Reflection.Emit.OperandType.InlineVar:
                reader.Offset += 2;
                break;
            case System.Reflection.Emit.OperandType.InlineI8:
            case System.Reflection.Emit.OperandType.InlineR:
                reader.Offset += 8;
                break;
            case System.Reflection.Emit.OperandType.InlineSwitch:
                var count = reader.ReadUInt32();
                reader.Offset += checked((int)(count * 4));
                break;
            default:
                // InlineBrTarget, InlineField, InlineI, InlineMethod, InlineSig, InlineString,
                // InlineTok, InlineType, ShortInlineR: four bytes each.
                reader.Offset += 4;
                break;
        }
    }

    /// <summary>The callee's declaring type and name, through a <see cref="MethodSpecification"/> to its definition.</summary>
    private static (string Type, string Member) Callee(MetadataReader md, EntityHandle token)
    {
        switch (token.Kind)
        {
            case HandleKind.MethodSpecification:
                return Callee(md, md.GetMethodSpecification((MethodSpecificationHandle)token).Method);
            case HandleKind.MethodDefinition:
                var definition = md.GetMethodDefinition((MethodDefinitionHandle)token);
                return (DefinitionFullName(md, definition.GetDeclaringType()), md.GetString(definition.Name));
            case HandleKind.MemberReference:
                var reference = md.GetMemberReference((MemberReferenceHandle)token);
                return (ParentFullName(md, reference.Parent), md.GetString(reference.Name));
            default:
                throw new InvalidOperationException($"a call's operand is a {token.Kind}, which is not a method; the decoder lost its place");
        }
    }

    /// <summary>A member reference's parent by name: a type reference, a type definition, or a generic instantiation's definition.</summary>
    private static string ParentFullName(MetadataReader md, EntityHandle parent)
    {
        if (TypeReferenceOf(md, parent) is { } reference)
            return FullNameOf(md, reference);

        if (parent.Kind == HandleKind.TypeDefinition)
            return DefinitionFullName(md, (TypeDefinitionHandle)parent);

        if (parent.Kind == HandleKind.TypeSpecification)
        {
            var blob = md.GetBlobReader(md.GetTypeSpecification((TypeSpecificationHandle)parent).Signature);
            if (blob.ReadSignatureTypeCode() == SignatureTypeCode.GenericTypeInstance)
            {
                blob.ReadCompressedInteger();
                var definition = blob.ReadTypeHandle();
                if (definition.Kind == HandleKind.TypeDefinition)
                    return DefinitionFullName(md, (TypeDefinitionHandle)definition);
            }
        }

        // An array's methods (Get, Set, Address) or a vararg method's parent: no banned callee is either.
        return "(" + parent.Kind + ")";
    }

    private static TypeDefinitionHandle Outermost(MetadataReader md, TypeDefinitionHandle handle)
    {
        while (md.GetTypeDefinition(handle).GetDeclaringType() is { IsNil: false } outer)
            handle = outer;
        return handle;
    }

    private static string DefinitionFullName(MetadataReader md, TypeDefinitionHandle handle)
    {
        var type = md.GetTypeDefinition(handle);
        var name = md.GetString(type.Name);
        if (type.GetDeclaringType() is { IsNil: false } outer)
            return DefinitionFullName(md, outer) + "+" + name;

        var ns = md.GetString(type.Namespace);
        return ns.Length == 0 ? name : ns + "." + name;
    }

    /// <summary>
    /// The method the source wrote: <c>Parse</c> for a lambda <c>&lt;Parse&gt;b__3_0</c> or a local
    /// function <c>&lt;Parse&gt;g__Local|3_0</c>, and for <c>MoveNext</c> on a state machine
    /// <c>&lt;ParseAsync&gt;d__5</c>, <c>ParseAsync</c>; otherwise the method's own name.
    /// </summary>
    private static string SourceMethodName(MetadataReader md, TypeDefinition type, string methodName)
    {
        if (Bracketed(methodName) is { } fromMethod)
            return fromMethod;

        for (var t = type; ; t = md.GetTypeDefinition(t.GetDeclaringType()))
        {
            if (Bracketed(md.GetString(t.Name)) is { Length: > 0 } fromType)
                return fromType;
            if (t.GetDeclaringType().IsNil)
                return methodName;
        }
    }

    /// <summary>The text inside a generated name's leading angle brackets, or null for a name the source wrote.</summary>
    private static string? Bracketed(string name)
    {
        if (!name.StartsWith('<'))
            return null;
        var close = name.IndexOf('>', StringComparison.Ordinal);
        return close > 0 ? name[1..close] : null;
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
