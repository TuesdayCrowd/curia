using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using NetArchTest.Rules;
using Xunit;

namespace Curia.Architecture.Tests;

/// <summary>
/// R10.63 (errata G17): the fence that keeps a stranger's words out of the reference client's own
/// voice, held as architecture rather than as review.
///
/// <para><b>Two halves, and each needs the other.</b> The CLI prints only through <c>Output</c>, and
/// <c>Output</c> takes a line only as a constant, an interpolation whose string holes are display
/// literals, a frame built the same way, or a reading of passages. The first fact below fails if
/// another type in the CLI writes to the console, which would step around the fence. The second
/// fails if a string parameter on <c>Output</c> or <c>FrameBuilder</c> loses
/// <see cref="ConstantExpectedAttribute"/>, which is what makes passing a served value as a line a
/// build error (CA1857) rather than a comment in a review.</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class OutputFenceTests
{
    private static Assembly Cli => Assembly.Load(new AssemblyName("curia"));

    private static Assembly Client => typeof(Curia.Client.FrameBuilder).Assembly;

    [Fact]
    public void R10_63_OnlyOutputWritesToTheConsole()
    {
        var types = Types.InAssembly(Cli).GetTypes().ToArray();
        Assert.Contains(types, t => t.Name == "Output");

        var result = Types.InAssembly(Cli)
            .That().DoNotHaveName("Output")
            .ShouldNot().HaveDependencyOn("System.Console")
            .GetResult();

        Assert.True(
            result.IsSuccessful,
            "types in the CLI other than Output write to the console, around R10.63's fence: "
            + string.Join(", ", result.FailingTypeNames ?? []));

        // And the rule is not vacuous: Output itself does.
        Assert.False(
            Types.InAssembly(Cli).That().HaveName("Output").ShouldNot().HaveDependencyOn("System.Console").GetResult().IsSuccessful,
            "Output does not write to the console, so the rule above constrains nothing; a defect in this fact");
    }

    [Fact]
    public void R10_63_EveryStringALineTakesMustBeAConstant()
    {
        var output = Cli.GetType("Curia.Client.Cli.Output", throwOnError: true)!;
        var builder = Client.GetType("Curia.Client.FrameBuilder", throwOnError: true)!;

        var checkedParameters = 0;
        var unfenced = new List<string>();

        foreach (var (type, flags) in new[]
        {
            (output, BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public),
            (builder, BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly),
        })
        {
            foreach (var method in type.GetMethods(flags).Where(m => !m.IsSpecialName))
            {
                foreach (var parameter in method.GetParameters().Where(p => p.ParameterType == typeof(string)))
                {
                    // The served span is the one string a frame takes that is not a constant: it is
                    // written as served only once its delimiters are checked, and quoted otherwise.
                    if (type == builder && method.Name == "Span" && parameter.Name == "rendered") continue;

                    checkedParameters++;
                    if (parameter.GetCustomAttribute<ConstantExpectedAttribute>() is null)
                        unfenced.Add($"{type.Name}.{method.Name}({parameter.Name})");
                }
            }
        }

        Assert.True(checkedParameters >= 4, $"only {checkedParameters} string parameters were found; the reflection is wrong");
        Assert.True(unfenced.Count == 0, "string parameters a variable can be passed to as a line: " + string.Join(", ", unfenced));
    }
}
