using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;

namespace Curia.Architecture.Tests;

/// <summary>
/// R6.60, R8.86 (errata G57, rulings 49 to 53): the guard builds a view only of what it could use. LCB39: <c>new PostView(</c> appears once in <c>src/</c>, in <c>ReadView</c>, so the legs and
/// <c>SubmissionGuard</c> build none and a post has one definition (trap 96). LCB28's counter, as a floor: <c>SubmissionGuard</c> has no call to <c>LogFacts.Extend</c> (the successor leg no longer copies the
/// read's posts); the allocation probes (LCB28, LCB35) carry the claim and this pins a name (trap 118).
/// </summary>
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed partial class SubmissionGuardFenceTests
{
    private static void Said(string expected, string actual)
    {
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
            throw new Xunit.Sdk.XunitException($"Expected: {expected}{Environment.NewLine}Actual:   {actual}");
    }

    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Curia.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Curia.sln not found above " + AppContext.BaseDirectory);
    }

    [GeneratedRegex(@"\bnew\s+PostView\s*\(", RegexOptions.CultureInvariant)]
    private static partial Regex NewPostView();

    /// <summary>LCB39. Exactly one <c>new PostView(</c> in the source of <c>src/</c>, and it is in the method <c>ReadView</c> of <c>PostProjection.cs</c>.</summary>
    [Fact]
    public void R8_86_LCB39_PostViewHasOneConstructorCallInSrc()
    {
        var sep = Path.DirectorySeparatorChar;
        var files = Directory.EnumerateFiles(Path.Combine(Root(), "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{sep}obj{sep}", StringComparison.Ordinal) && !f.Contains($"{sep}bin{sep}", StringComparison.Ordinal))
            .ToArray();
        var calls = new List<string>();
        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            foreach (Match m in NewPostView().Matches(text))
            {
                var declaration = text.LastIndexOf("private static PostView? ReadView(", m.Index, StringComparison.Ordinal);
                calls.Add(Path.GetFileName(file) + (declaration >= 0 && file.EndsWith("PostProjection.cs", StringComparison.Ordinal) ? " (ReadView)" : " (elsewhere)"));
            }
        }

        Said("1 construction of PostView in src: PostProjection.cs (ReadView)", $"{calls.Count} construction{(calls.Count == 1 ? string.Empty : "s")} of PostView in src: {string.Join(", ", calls)}");
    }

    /// <summary>LCB28 (the counter, as a floor). <c>SubmissionGuard</c>, its closures included, has no call to <c>LogFacts.Extend</c>.</summary>
    [Fact]
    public void R6_60_LCB28_TheSubmissionGuardDoesNotExtendTheFacts()
    {
        using var module = ModuleDefinition.ReadModule(Path.Combine(AppContext.BaseDirectory, "Curia.Application.dll"));
        var type = module.GetType("Curia.Application.Ingest.SubmissionGuard") ?? throw new InvalidOperationException("Curia.Application has no SubmissionGuard");
        var calls = 0;

        void Walk(TypeDefinition t)
        {
            foreach (var method in t.Methods.Where(m => m.HasBody))
                foreach (var instruction in method.Body.Instructions)
                    if (instruction.OpCode.Code is Code.Call or Code.Callvirt && instruction.Operand is MethodReference callee
                        && callee.Name == "Extend" && callee.DeclaringType.FullName == "Curia.Application.Ingest.LogFacts")
                        calls++;

            foreach (var nested in t.NestedTypes) Walk(nested);
        }

        Walk(type);
        Said("0 calls to LogFacts.Extend in SubmissionGuard", $"{calls} call{(calls == 1 ? string.Empty : "s")} to LogFacts.Extend in SubmissionGuard");
    }
}
