using System.Collections.Immutable;
using System.Reflection;
using System.Text;

namespace Curia.Infrastructure.Migrations;

/// <summary>
/// The whole forward-only migration set in <c>db/</c>, in order, rendered as one executable
/// script.
///
/// <para>Exists because there is now more than one migration. <see cref="EventStoreSchema"/>
/// knows about <c>0001_create_events.sql</c> specifically and stays that way -- its remarks are
/// about that file's Appendix D provenance and are worth keeping attached to it -- but a
/// consumer provisioning a database wants <i>every</i> migration, applied in numeric order, and
/// a consumer that names them one at a time is a consumer that will one day forget the newest.
/// That is exactly how a test fixture ends up green against a schema no deployment has.</para>
///
/// <para>Order is the file-name order, and the names are listed explicitly rather than
/// discovered by enumerating manifest resources: <see cref="Assembly.GetManifestResourceNames"/>
/// makes no ordering guarantee, and "forward-only, numbered" is a promise about sequence. A new
/// migration is one line here, and forgetting that line fails the Infrastructure tests rather
/// than passing quietly.</para>
/// </summary>
public static class SchemaMigrations
{
    private const string ResourcePrefix = "Curia.Infrastructure.Migrations.";
    private const string PasswordToken = "__CURIA_APP_ROLE_PASSWORD__";

    /// <summary>db/0002: the operational tables (replay cache, DPoP nonces, agent keys) and their
    /// grants. Named because it is the one migration a caller has reason to render on its own --
    /// see Curia.Infrastructure.Tests' per-test schema isolation.</summary>
    public const string OperationalStateFile = "0002_create_operational_state.sql";

    /// <summary>
    /// Every migration file in <c>db/</c>, in the order it must be applied. The names are the
    /// checked-in file names, so a reader comparing this list against a directory listing can
    /// see at a glance whether one is missing.
    /// </summary>
    public static ImmutableArray<string> FileNames { get; } =
    [
        EventStoreSchema.FileName,
        OperationalStateFile,
    ];

    /// <summary>
    /// One migration's raw, unrendered template text, read from the embedded resource so it
    /// works regardless of the process's current directory (see
    /// <see cref="EventStoreSchema.LoadTemplate"/> for why embedding rather than reading
    /// <c>db/</c> from disk).
    /// </summary>
    public static string LoadTemplate(string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

        var logicalName = ResourcePrefix + fileName;
        var assembly = typeof(SchemaMigrations).Assembly;
        using var stream = assembly.GetManifestResourceStream(logicalName)
            ?? throw new InvalidOperationException(
                $"Embedded resource '{logicalName}' was not found in {assembly.FullName}. " +
                $"Check Curia.Infrastructure.csproj's EmbeddedResource/LogicalName for db/{fileName}.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// One migration, rendered with a real role name and -- for the one migration that creates
    /// the role -- a real password.
    ///
    /// <para><paramref name="rolePassword"/> is optional because only
    /// <c>0001_create_events.sql</c> issues <c>CREATE ROLE</c>; every later migration merely
    /// grants to a role that already exists, and demanding a password for one of those would mean
    /// a caller inventing a value that goes nowhere. Omitting it against a template that <i>does</i>
    /// carry the password placeholder is refused rather than silently rendered, because a
    /// <c>CREATE ROLE ... PASSWORD '__CURIA_APP_ROLE_PASSWORD__'</c> that reached Postgres
    /// unsubstituted would create a real, loginable role with a password printed in this
    /// repository.</para>
    /// </summary>
    public static string Render(string fileName, string roleName, string? rolePassword = null)
    {
        var template = LoadTemplate(fileName);

        if (rolePassword is not null)
            return EventStoreSchema.Render(template, roleName, rolePassword);

        if (template.Contains(PasswordToken, StringComparison.Ordinal))
        {
            throw new ArgumentNullException(
                nameof(rolePassword),
                $"Migration '{fileName}' creates the application role and therefore needs a password; " +
                "rendering it without one would leave the placeholder token in executable SQL.");
        }

        // Rendered through the same call the password-carrying path uses, so the role-name
        // validation (a plain lowercase identifier, because it is substituted unquoted) is not
        // duplicated and cannot drift. The password argument is a placeholder this template has
        // no occurrence of, so the substitution is a no-op by construction.
        return EventStoreSchema.Render(template, roleName, "-");
    }

    /// <summary>
    /// Every migration, in order, rendered with a real role name and password and concatenated
    /// into a single script a Postgres connection can execute in one command.
    ///
    /// <para>Concatenated rather than executed one file at a time because the caller that wants
    /// this wants a provisioned database, not a migration runner: applying half the set and
    /// stopping leaves a database that is neither the old shape nor the new one. A real
    /// deployment's migration tool (the scoping document names dbup-postgresql) tracks which
    /// files it has applied and is the right answer once there is a database it must not
    /// re-provision from scratch; this is the right answer for provisioning one from nothing,
    /// which is what every fixture in this solution does.</para>
    /// </summary>
    public static string RenderAll(string roleName, string rolePassword)
    {
        var script = new StringBuilder();
        foreach (var fileName in FileNames)
        {
            // Blank-line separated and named, so a Postgres error message's statement text can
            // be traced back to the file it came from without counting semicolons.
            script.Append("-- >>> ").Append(fileName).Append('\n');
            script.Append(Render(fileName, roleName, rolePassword));
            script.Append("\n\n");
        }

        return script.ToString();
    }
}
