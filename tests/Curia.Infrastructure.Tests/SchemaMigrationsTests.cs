using Curia.Infrastructure.Migrations;
using Xunit;

namespace Curia.Infrastructure.Tests;

/// <summary>
/// <see cref="SchemaMigrations"/>' ordering list against the migrations actually checked in.
///
/// <para>The <c>&lt;EmbeddedResource&gt;</c> in Curia.Infrastructure.csproj globs <c>db/*.sql</c>,
/// so a new migration file is embedded automatically -- but <see cref="SchemaMigrations.FileNames"/>
/// is hand-listed, because "forward-only, numbered" is a promise about sequence and
/// <c>GetManifestResourceNames</c> guarantees no order. That combination has one failure mode: a
/// migration added to <c>db/</c> and not to the list, which every fixture would then silently
/// decline to apply while the suite stayed green against a schema no deployment has. This test is
/// the thing that makes that impossible rather than merely discouraged.</para>
/// </summary>
public sealed class SchemaMigrationsTests
{
    [Fact]
    public void FileNamesCoversEveryCheckedInMigrationInOrder()
    {
        var expected = Directory
            .EnumerateFiles(Path.Combine(RepoRoot(), "db"), "*.sql")
            .Select(Path.GetFileName)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.NotEmpty(expected); // guards against pointing at an empty or wrong directory
        Assert.Equal(expected, SchemaMigrations.FileNames);
    }

    /// <summary>
    /// Every listed migration really loads from the embedded resources, so a name in the list that
    /// no longer matches a file fails here rather than at the first fixture that tries to
    /// provision a database.
    /// </summary>
    [Fact]
    public void EveryListedMigrationLoads()
    {
        foreach (var fileName in SchemaMigrations.FileNames)
            Assert.NotEmpty(SchemaMigrations.LoadTemplate(fileName));
    }

    /// <summary>
    /// A migration rendered without a password must not still contain the password placeholder --
    /// which would otherwise reach Postgres as a real, loginable role whose password is printed in
    /// this repository. The refusal is what makes the optional parameter safe.
    /// </summary>
    [Fact]
    public void RenderingTheRoleCreatingMigrationWithoutAPasswordIsRefused()
    {
        Assert.Throws<ArgumentNullException>(
            () => SchemaMigrations.Render("0001_create_events.sql", "curia_app_test"));
    }

    [Fact]
    public void RenderedMigrationsCarryNoRemainingPlaceholders()
    {
        var rendered = SchemaMigrations.RenderAll("curia_app_test", "a-generated-password");

        Assert.DoesNotContain("__CURIA_APP_ROLE__", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("__CURIA_APP_ROLE_PASSWORD__", rendered, StringComparison.Ordinal);
        Assert.Contains("curia_app_test", rendered, StringComparison.Ordinal);
    }

    /// <summary>
    /// Walks up from the test assembly's output directory to the repo root (the first ancestor
    /// containing a <c>db</c> directory), mirroring the path-discovery idiom
    /// <c>Curia.Canon.Tests.Vectors.VectorLoader</c> and <c>Curia.Architecture.Tests</c> already
    /// use to locate checked-in files without a hardcoded absolute path.
    /// </summary>
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "db")))
            dir = dir.Parent;

        return dir?.FullName
            ?? throw new InvalidOperationException("Could not find repo root (a 'db' directory) above " + AppContext.BaseDirectory);
    }
}
