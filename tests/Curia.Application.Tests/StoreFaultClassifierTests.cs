using System.Diagnostics.CodeAnalysis;
using Curia.Application.Ports;
using Curia.Application.Tests.InMemory;
using Xunit;

namespace Curia.Application.Tests;

/// <summary>
/// ULG12, its in-memory half (errata G52, R11.71): the in-memory adapter of <see cref="IStoreFaultClassifier"/> names no exception type, so it answers false
/// for a timeout, for an exception wrapping one and for the log's own exception. The predicate's truth table, which names the driver's types, is in
/// <c>Curia.Infrastructure.Tests</c>. A control: green on the seam tree, because the port and this adapter exist.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class StoreFaultClassifierTests
{
    [Fact]
    public void R11_71_ULG12_TheInMemoryClassifierNamesNoExceptionAndAnswersFalseForEveryOne()
    {
        IStoreFaultClassifier classifier = new InMemoryStoreFaultClassifier();
        Exception[] faults =
        [
            new TimeoutException("a timeout"),
            new InvalidOperationException("a wrapper", new TimeoutException("inside")),
            new AggregateException(new TimeoutException("inside an aggregate")),
            new LogUnreadableException("armed", new TimeoutException("inside the log's own exception")),
            new InvalidOperationException("a bug"),
        ];

        var answered = faults.Where(classifier.IsUnavailable).Select(f => f.GetType().Name).ToArray();

        Assert.True(answered.Length == 0, "the in-memory classifier named a fault: " + string.Join(", ", answered));
    }

    [Fact]
    public void R11_71_ULG12_TheLogsExceptionCarriesItsCauseAndTheDriversExceptionInside()
    {
        var inner = new TimeoutException("the driver said so");
        var fault = new LogUnreadableException("TimeoutException", inner);

        Assert.True(ReferenceEquals(fault.InnerException, inner) && fault.Cause == "TimeoutException", $"cause {fault.Cause}, inner {fault.InnerException?.GetType().Name}");
    }
}
