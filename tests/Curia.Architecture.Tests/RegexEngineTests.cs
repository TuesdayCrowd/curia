using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;

namespace Curia.Architecture.Tests;

/// <summary>
/// R10.69 (errata G18): <i>"a test SHALL fail when any pattern on the screening path runs on another
/// engine."</i>
///
/// <para><b>Why the Domain reflection fact is not enough.</b>
/// <c>ScreeningCostTests.R10_69_EveryScreeningPatternRunsOnTheLinearEngine</c> reads only the static
/// parameterless methods of <c>Curia.Domain.Screening</c>, so a <c>static readonly Regex</c> field, a
/// <c>new Regex</c> local or a static <c>Regex.IsMatch(input, pattern)</c> call is invisible to it
/// (review of 1b0d423). The screening path cannot be computed statically -- the reference client
/// screens too -- so this fact makes the rule structural instead: it reads the IL of every assembly
/// built from <c>src/</c>, and the one way a <see cref="Regex"/> may come into being there is a
/// <c>[GeneratedRegex]</c> pattern with <see cref="RegexOptions.NonBacktracking"/>.</para>
///
/// <para><b>What is allowed.</b> A construction site, or a type deriving from <see cref="Regex"/>, is
/// allowed only when it, or a type enclosing it, is in <c>System.Text.RegularExpressions.Generated</c>
/// <i>and</i> carries <c>GeneratedCodeAttribute</c> naming the tool
/// <c>System.Text.RegularExpressions.Generator</c>: the types the regex generator emits and marks, as
/// observed in Curia.Domain.dll in Release and Debug alike (one type per pattern, deriving from
/// <see cref="Regex"/>, constructing itself in its <c>.cctor</c>). It is a rule, not a list of today's
/// sites. A <c>[GeneratedRegex]</c> partial method's body is written by the generator and holds no
/// site of its own, so the attribute on a method is not itself an allowance.</para>
///
/// <para><b>Fifteen, and no silent exception.</b> The attributed methods are counted. A future pattern
/// that truly needs a backtracking construct must be excluded here, by name, with its reason written
/// in this fact -- never allowed by loosening the rule. (A <c>[GeneratedRegex]</c> partial property,
/// which this count does not read, would still turn the non-vacuity count below red.)</para>
///
/// <para><b>Not vacuous.</b> On the shipped side, the generator types the rule allows must number
/// exactly the attributed methods: if an SDK bump changes the generator's shape, that goes red, and
/// the remedy is to re-observe the generator, not to loosen the assertion. On the self side,
/// <see cref="Bypasses"/> holds one bypass of each shape on purpose, and the same predicate must name
/// exactly those three. The self-scan reads <see cref="Bypasses"/> alone rather than this whole
/// assembly, because <c>MutationResidueTests</c> legitimately holds backtracking patterns (its
/// self-comparison needs a <c>\1</c> backreference, which the linear engine does not support), and an
/// exactness claim over the assembly would tie this guard to unrelated test code.</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class RegexEngineTests
{
    private const string RegexType = "System.Text.RegularExpressions.Regex";
    private const string GeneratedNamespace = "System.Text.RegularExpressions.Generated";
    private const string GeneratedCode = "System.CodeDom.Compiler.GeneratedCodeAttribute";
    private const string GeneratorTool = "System.Text.RegularExpressions.Generator";
    private const string GeneratedRegex = "System.Text.RegularExpressions.GeneratedRegexAttribute";

    /// <summary>8 in SecretScanner, 6 in InjectionDetector, 1 in DerivedViews.</summary>
    private const int ExpectedPatternCount = 15;

    /// <summary>The static <see cref="Regex"/> methods that build a pattern from a string argument.</summary>
    private static readonly HashSet<string> StaticPatternMethods = new(StringComparer.Ordinal)
    {
        "IsMatch", "Match", "Matches", "Replace", "Split", "Count", "EnumerateMatches", "EnumerateSplits",
    };

    [Fact]
    public void R10_69_EveryRegexInShippedCodeIsAGeneratedPatternOnTheLinearEngine()
    {
        var shipped = Shipped.ShippedAssemblies();
        Assert.True(shipped.Count >= 12, $"only {shipped.Count} projects were found under src/; the scan is looking in the wrong place");

        var assemblies = shipped.Select(path => AssemblyDefinition.ReadAssembly(path)).ToList();
        try
        {
            var types = assemblies.SelectMany(a => a.MainModule.GetTypes()).ToList();
            // A name several assemblies share (<Module>, Program) resolves to the first; only a chain
            // that reaches Regex matters here, and the generator's types carry a per-assembly hash.
            var known = new Dictionary<string, TypeDefinition>(StringComparer.Ordinal);
            foreach (var type in types)
                known.TryAdd(type.FullName, type);

            // Every part is checked before any is asserted, so one message names every failure.
            var failures = new List<string>();

            // (b), (c): every construction site and every type deriving from Regex is the generator's.
            var offenders = Offenders(types, known);
            if (offenders.Count > 0)
            {
                failures.Add(
                    "a Regex is built in shipped code other than by a [GeneratedRegex] pattern, so it may run on a backtracking engine (R10.69): "
                    + string.Join("; ", offenders));
            }

            // (d): every generated pattern is on the linear engine.
            var attributed = types
                .SelectMany(t => t.Methods)
                .Select(m => (Name: $"{m.DeclaringType.FullName}.{m.Name}", Attribute: m.CustomAttributes.FirstOrDefault(a => a.AttributeType.FullName == GeneratedRegex)))
                .Where(m => m.Attribute is not null)
                .ToList();
            var backtracking = attributed
                .Where(m => (Options(m.Attribute!) & RegexOptions.NonBacktracking) == 0)
                .Select(m => m.Name)
                .ToList();
            if (backtracking.Count > 0)
                failures.Add("a [GeneratedRegex] pattern lacks RegexOptions.NonBacktracking (R10.69): " + string.Join(", ", backtracking));

            // (e): fifteen, by name.
            var names = attributed.Select(m => m.Name).OrderBy(n => n, StringComparer.Ordinal).ToList();
            if (names.Count != ExpectedPatternCount)
                failures.Add($"expected {ExpectedPatternCount} [GeneratedRegex] methods in shipped code, found {names.Count}: {string.Join(", ", names)}");

            // (f), shipped side: the rule allows exactly the generator's types, one per attributed method.
            var allowedDerived = types.Count(t => DerivesFromRegex(t, known) && IsGeneratorEmitted(t));
            if (allowedDerived != names.Count)
            {
                failures.Add(
                    $"{allowedDerived} generator-emitted types derive from Regex against {names.Count} [GeneratedRegex] methods; "
                    + "the generator's shape has changed, so re-observe it rather than loosen this assertion");
            }

            Assert.True(failures.Count == 0, string.Join(" | ", failures));
        }
        finally
        {
            foreach (var assembly in assemblies)
                assembly.Dispose();
        }

        // (f), self side: the predicate names exactly the three bypasses held below, and nothing else in Bypasses.
        Assert.NotNull(Bypasses.Field);
        Assert.NotNull(Bypasses.Constructed());
        Assert.True(Bypasses.Called("aab"));

        using var self = AssemblyDefinition.ReadAssembly(typeof(RegexEngineTests).Assembly.Location);
        var bypasses = self.MainModule.GetType(typeof(RegexEngineTests).FullName + "/" + nameof(Bypasses));
        Assert.NotNull(bypasses);
        var own = Offenders([bypasses], new Dictionary<string, TypeDefinition>(StringComparer.Ordinal))
            .Select(o => o[..o.IndexOf(' ', StringComparison.Ordinal)])
            .OrderBy(o => o, StringComparer.Ordinal)
            .ToList();
        var site = bypasses.FullName + "::";
        Assert.Equal(
            [site + ".cctor", site + nameof(Bypasses.Called), site + nameof(Bypasses.Constructed)],
            own);
    }

    /// <summary>
    /// Every construction site and every type deriving from <see cref="Regex"/> in
    /// <paramref name="types"/> that the generator did not emit, each as <c>Type::Method opcode
    /// target</c> or <c>Type derives from Regex</c>. <paramref name="known"/> resolves base types
    /// within the scanned code; a base outside it is a framework type, and the framework has no public
    /// type deriving from <see cref="Regex"/>.
    /// </summary>
    private static List<string> Offenders(IEnumerable<TypeDefinition> types, Dictionary<string, TypeDefinition> known)
    {
        var offenders = new List<string>();
        foreach (var type in types)
        {
            if (IsGeneratorEmitted(type))
                continue;

            if (DerivesFromRegex(type, known))
                offenders.Add($"{type.FullName} derives from Regex (R10.69)");

            foreach (var method in type.Methods.Where(m => m.HasBody))
            foreach (var instruction in method.Body.Instructions)
            {
                if (instruction.Operand is not MethodReference target || !IsConstruction(instruction.OpCode.Code, target, known))
                    continue;

                offenders.Add($"{type.FullName}::{method.Name} {instruction.OpCode} {target.FullName} (R10.69)");
            }
        }

        return offenders;
    }

    /// <summary>
    /// A <c>newobj</c> of <see cref="Regex"/> or of a type deriving from it, or a call to one of
    /// <see cref="Regex"/>'s static pattern methods.
    /// </summary>
    private static bool IsConstruction(Code code, MethodReference target, Dictionary<string, TypeDefinition> known)
    {
        var declaring = target.DeclaringType.GetElementType().FullName;
        if (code == Code.Newobj)
            return declaring == RegexType || (known.TryGetValue(declaring, out var created) && DerivesFromRegex(created, known));

        return code is Code.Call or Code.Callvirt
            && declaring == RegexType
            && !target.HasThis
            && StaticPatternMethods.Contains(target.Name);
    }

    private static bool DerivesFromRegex(TypeDefinition type, Dictionary<string, TypeDefinition> known)
    {
        for (var baseType = type.BaseType; baseType is not null;)
        {
            var name = baseType.GetElementType().FullName;
            if (name == RegexType)
                return true;
            if (!known.TryGetValue(name, out var definition))
                return false;
            baseType = definition.BaseType;
        }

        return false;
    }

    /// <summary>The type, or one enclosing it, is in the generator's namespace and marked as its output.</summary>
    private static bool IsGeneratorEmitted(TypeDefinition type)
    {
        for (var current = type; current is not null; current = current.DeclaringType)
        {
            if (current.Namespace == GeneratedNamespace
                && current.CustomAttributes.Any(a =>
                    a.AttributeType.FullName == GeneratedCode
                    && a.ConstructorArguments.Count > 0
                    && a.ConstructorArguments[0].Value is GeneratorTool))
                return true;
        }

        return false;
    }

    /// <summary>The <see cref="RegexOptions"/> a <c>[GeneratedRegex]</c> passes, or none when it passes only a pattern.</summary>
    private static RegexOptions Options(CustomAttribute attribute) =>
        attribute.ConstructorArguments.Count > 1 && attribute.ConstructorArguments[1].Value is int options
            ? (RegexOptions)options
            : RegexOptions.None;

    /// <summary>
    /// One bypass of each shape the fact reads, held here so the fact can show it finds them: a
    /// field initializer (which lands in <c>.cctor</c>), a <c>new Regex</c>, and a static call.
    /// </summary>
    private static class Bypasses
    {
        internal static readonly Regex Field = new("a+b", RegexOptions.CultureInvariant);

        internal static Regex Constructed() => new("a+b", RegexOptions.CultureInvariant);

        internal static bool Called(string s) => Regex.IsMatch(s, "a+b");
    }
}
