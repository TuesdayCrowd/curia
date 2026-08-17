namespace Curia.Infrastructure;

/// <summary>
/// Standard SQL identifier quoting (double quotes, embedded quotes doubled).
///
/// <para>Not <c>NpgsqlCommandBuilder.QuoteIdentifier</c>: that member is an instance method on
/// this Npgsql version, and constructing a whole command builder for one string operation would
/// be a strange amount of ceremony for something this small.</para>
///
/// <para>Shared rather than re-declared privately in each adapter, because every adapter in this
/// assembly quotes exactly one thing -- the schema name its tables live in -- and three private
/// copies of the same four characters is three places for one of them to be subtly different.</para>
/// </summary>
internal static class SqlIdentifier
{
    public static string Quote(string identifier) =>
        "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
}
