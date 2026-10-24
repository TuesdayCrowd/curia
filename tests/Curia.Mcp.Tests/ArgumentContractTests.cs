using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Curia.Mcp;
using Xunit;

namespace Curia.Mcp.Tests;

/// <summary>
/// R11.37 (errata G27, ruling 3): the checker as a pure function, over schemas written here and the faults
/// each call must produce, written here. The oracle is the JSON Schema each fact states beside its call, never
/// a tool's own schema (that is <see cref="ToolProtocolTests"/>'s subject, through the protocol).
/// </summary>
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class ArgumentContractTests
{
    /// <summary>Two levels, every type the tools use, one required member.</summary>
    private const string Schema = """
        {"type":"object","required":["board"],"properties":{
          "board":{"type":"string"},
          "limit":{"type":"integer"},
          "ratio":{"type":"number"},
          "flag":{"type":"boolean"},
          "maybe":{"type":["string","null"]},
          "tags":{"type":"array","items":{"type":"string"}},
          "criteria":{"type":"object","properties":{"kinds":{"type":"array","items":{"type":"string"}},"limit":{"type":"integer"}}}}}
        """;

    private static ImmutableArray<ArgumentFault> Check(string schema, string arguments, Func<string, string?>? reasonFor = null)
    {
        using var schemaDocument = JsonDocument.Parse(schema);
        using var argumentsDocument = JsonDocument.Parse(arguments);
        var pairs = argumentsDocument.RootElement.EnumerateObject().Select(m => new KeyValuePair<string, JsonElement>(m.Name, m.Value)).ToArray();
        return ArgumentContract.Check(schemaDocument.RootElement, pairs, reasonFor);
    }

    [Fact]
    public void R11_37_ACallThatMatchesTheSchemaHasNoFault()
    {
        Assert.Empty(Check(Schema, """{"board":"b","limit":3,"tags":["a"],"criteria":{"kinds":["x"],"limit":2}}"""));
    }

    [Fact]
    public void R11_37_AnUnknownMemberAtDepthOneIsAFaultNamingTheMembersTheSchemaDefines()
    {
        var fault = Assert.Single(Check(Schema, """{"board":"b","frobnicate":true}"""));
        Assert.Equal("frobnicate", fault.Path);
        Assert.Equal(ArgumentFaultKind.Unknown, fault.Kind);
        foreach (var member in new[] { "board", "limit", "criteria" }) Assert.Contains(member, fault.Expected, StringComparison.Ordinal);
    }

    [Fact]
    public void R11_37_AnUnknownMemberAtDepthTwoIsAFaultAtItsDottedPath()
    {
        var fault = Assert.Single(Check(Schema, """{"board":"b","criteria":{"frobnicate":1}}"""));
        Assert.Equal("criteria.frobnicate", fault.Path);
        Assert.Equal(ArgumentFaultKind.Unknown, fault.Kind);
        Assert.Contains("kinds", fault.Expected, StringComparison.Ordinal);
        Assert.DoesNotContain("board", fault.Expected, StringComparison.Ordinal);
    }

    [Fact]
    public void R9_26_AnUnknownMemberCarriesTheReasonTheCallerHasForIt()
    {
        var faults = Check(Schema, """{"board":"b","criteria":{"unresolved_only":true,"frobnicate":1}}""", name => name == "unresolved_only" ? "waits on R9.9" : null);
        Assert.Equal(2, faults.Length);
        Assert.Equal("waits on R9.9", faults.Single(f => f.Path == "criteria.unresolved_only").Reason);
        Assert.Null(faults.Single(f => f.Path == "criteria.frobnicate").Reason);
    }

    [Fact]
    public void R11_37_AMissingRequiredMemberIsAFaultNamingIt()
    {
        var fault = Assert.Single(Check(Schema, """{"limit":3}"""));
        Assert.Equal("board", fault.Path);
        Assert.Equal(ArgumentFaultKind.Missing, fault.Kind);
    }

    [Fact]
    public void R11_37_NoArgumentsAtAllMissesTheRequiredMember()
    {
        using var schemaDocument = JsonDocument.Parse(Schema);
        var fault = Assert.Single(ArgumentContract.Check(schemaDocument.RootElement, arguments: null));
        Assert.Equal(("board", ArgumentFaultKind.Missing), (fault.Path, fault.Kind));
    }

    [Theory]
    [InlineData("""{"board":1}""", "board", "string")]
    [InlineData("""{"board":"b","limit":"text"}""", "limit", "integer")]
    [InlineData("""{"board":"b","limit":1.5}""", "limit", "integer")]
    [InlineData("""{"board":"b","ratio":"text"}""", "ratio", "number")]
    [InlineData("""{"board":"b","flag":"yes"}""", "flag", "boolean")]
    [InlineData("""{"board":"b","tags":"a"}""", "tags", "array")]
    [InlineData("""{"board":"b","criteria":"a"}""", "criteria", "object")]
    [InlineData("""{"board":"b","criteria":{"kinds":"a"}}""", "criteria.kinds", "array")]
    [InlineData("""{"board":"b","criteria":{"limit":true}}""", "criteria.limit", "integer")]
    public void R11_37_AValueOfATypeTheSchemaDoesNotAdmitIsAFaultAtItsPath(string arguments, string path, string expected)
    {
        var fault = Assert.Single(Check(Schema, arguments));
        Assert.Equal(path, fault.Path);
        Assert.Equal(ArgumentFaultKind.WrongType, fault.Kind);
        Assert.Contains(expected, fault.Expected, StringComparison.Ordinal);
    }

    [Fact]
    public void R11_37_AnArrayElementOfTheWrongTypeIsAFaultAtItsIndex()
    {
        var fault = Assert.Single(Check(Schema, """{"board":"b","tags":["a",2],"criteria":{"kinds":[true]}}""").Where(f => f.Path == "tags[1]"));
        Assert.Equal(ArgumentFaultKind.WrongType, fault.Kind);
        Assert.Contains(Check(Schema, """{"board":"b","criteria":{"kinds":[true]}}"""), f => f.Path == "criteria.kinds[0]");
    }

    [Fact]
    public void R11_37_NullIsAdmittedWhereTheSchemaIsNullableAndAFaultWhereItIsNot()
    {
        Assert.Empty(Check(Schema, """{"board":"b","maybe":null}"""));

        var fault = Assert.Single(Check(Schema, """{"board":"b","tags":null}"""));
        Assert.Equal(("tags", ArgumentFaultKind.WrongType), (fault.Path, fault.Kind));
    }

    [Theory]
    [InlineData("""{"board":"b","limit":"3"}""")]
    [InlineData("""{"board":"b","ratio":"2.5"}""")]
    [InlineData("""{"board":"b","criteria":{"limit":"3"}}""")]
    public void R11_37_ANumericStringIsAdmittedWhereTheBinderAdmitsOne(string arguments)
    {
        Assert.Empty(Check(Schema, arguments));
    }

    [Fact]
    public void R11_37_AMemberWithoutATypeAdmitsAnyValue()
    {
        Assert.Empty(Check("""{"type":"object","properties":{"any":{}}}""", """{"any":[1,"a",null]}"""));
    }

    [Fact]
    public void R11_37_ASchemaWithNoPropertiesDefinesNothingSoEveryMemberIsUnknown()
    {
        var fault = Assert.Single(Check("""{"type":"object"}""", """{"a":1}"""));
        Assert.Equal(("a", ArgumentFaultKind.Unknown), (fault.Path, fault.Kind));
    }

    [Fact]
    public void R9_25_NoFaultCarriesTheValueTheCallerSent()
    {
        const string secret = "SECRET-VALUE-7731";
        var faults = Check(Schema, $$$"""{"board":9,"limit":"{{{secret}}}","tags":["a",["{{{secret}}}"]],"criteria":{"kinds":"{{{secret}}}","unknown_member":"{{{secret}}}"}}""");
        Assert.NotEmpty(faults);

        foreach (var fault in faults)
        {
            Assert.DoesNotContain(secret, fault.Path, StringComparison.Ordinal);
            Assert.DoesNotContain(secret, fault.Expected, StringComparison.Ordinal);
            Assert.DoesNotContain(secret, fault.Reason ?? string.Empty, StringComparison.Ordinal);
        }
    }
}
