namespace Curia.Infrastructure.Migrations;

/// <summary>
/// Loads and renders <c>db/0001_create_events.sql</c> -- the Stage 2 migration that creates
/// both the <c>events</c> table (Appendix D verbatim) and the R11.6 application role
/// constrained to it (see that file's own header comment for why the role/grants live in the
/// migration rather than being assumed to pre-exist).
///
/// The file itself is not directly executable SQL: it names the application role with two
/// literal placeholder tokens, <c>__CURIA_APP_ROLE__</c> and
/// <c>__CURIA_APP_ROLE_PASSWORD__</c>, rather than a real role name and secret. A single,
/// checked-in migration cannot hard-code a role name and still let every consumer use a
/// distinct one -- a real deployment needs its own conventional name and a generated secret,
/// and Curia.Infrastructure.Tests' PostgresDatabaseFixture needs a fresh, unique name and
/// password *every test run*, so that concurrent runs (a developer's machine and CI, or two
/// CI jobs) never fight over one cluster-wide role. <see cref="Render"/> is the one place that
/// substitution happens, so every consumer renders the identical template rather than
/// maintaining a hand-copied variant that can drift from what Appendix D actually says.
/// </summary>
public static class EventStoreSchema
{
    /// <summary>db/0001: the events table and the R11.6-constrained role. Also
    /// <see cref="SchemaMigrations.FileNames"/>' first entry -- the ordering list is the authority
    /// on what runs when; this constant is only how <i>this</i> type names the file it is about.</summary>
    public const string FileName = "0001_create_events.sql";

    private const string RoleToken = "__CURIA_APP_ROLE__";
    private const string PasswordToken = "__CURIA_APP_ROLE_PASSWORD__";

    /// <summary>
    /// Reads this migration's raw, unrendered template text from the embedded resource --
    /// works regardless of the process's current directory, unlike reading the checked-in
    /// db/ file from disk by walking up from AppContext.BaseDirectory the way the test
    /// projects' conformance-vector loaders do (a technique that only makes sense for tests
    /// running inside this repository's checkout, not for this production assembly).
    ///
    /// Delegates to <see cref="SchemaMigrations.LoadTemplate"/> now that there is more than one
    /// migration: two copies of "find the embedded resource, or explain which csproj item is
    /// missing" is one copy too many, and the explanation is the part worth having in one place.
    /// </summary>
    public static string LoadTemplate() => SchemaMigrations.LoadTemplate(FileName);

    /// <summary>
    /// Substitutes the role-name and password placeholder tokens in <paramref name="template"/>
    /// (ordinarily <see cref="LoadTemplate"/>'s own output) with real values, producing SQL a
    /// Postgres connection can execute directly. A straight text substitution, not a SQL-aware
    /// templating engine: the two tokens are deliberately distinctive (unlikely to collide with
    /// real content) and this keeps the migration file itself plain, reviewable SQL rather than
    /// SQL embedding a second template syntax.
    /// </summary>
    public static string Render(string template, string roleName, string rolePassword)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(template);
        ArgumentException.ThrowIfNullOrWhiteSpace(roleName);
        ArgumentException.ThrowIfNullOrWhiteSpace(rolePassword);

        // Postgres role identifiers are unquoted below (CREATE ROLE <token> ...), so a caller
        // supplying anything but a plain identifier would produce invalid or, worse,
        // SQL-injectable text; every caller in this solution generates roleName itself
        // (never from untrusted input), so this is a fail-fast guard against a caller
        // mistake, not a defense against an adversarial value.
        foreach (var c in roleName)
        {
            if (!char.IsAsciiLetterLower(c) && !char.IsAsciiDigit(c) && c != '_')
            {
                throw new ArgumentException(
                    $"roleName '{roleName}' must be a plain lowercase identifier " +
                    "([a-z0-9_]) -- it is substituted into SQL unquoted.",
                    nameof(roleName));
            }
        }

        // The password lands inside a single-quoted SQL string literal (PASSWORD '...'), so an
        // embedded single quote must be escaped by doubling it (standard SQL literal escaping)
        // rather than rejected -- a generated password is not guaranteed to avoid the character.
        var escapedPassword = rolePassword.Replace("'", "''", StringComparison.Ordinal);

        return template
            .Replace(RoleToken, roleName, StringComparison.Ordinal)
            .Replace(PasswordToken, escapedPassword, StringComparison.Ordinal);
    }
}
