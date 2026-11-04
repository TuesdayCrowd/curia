using Curia.Domain.Primitives;

namespace Curia.Client;

/// <summary>
/// R11.84 (errata G60): the one definition of the sentences a client says about a reference to a secret, so that the refusal, every write tool's description
/// and <c>curia help</c> cannot drift apart. The words are the errata's, character for character; a test reads them from there and compares.
/// </summary>
public static class CredentialWording
{
    /// <summary>
    /// What a refusal of credential material whose categories include <c>ConnectionStringPassword</c> adds after the instruction to rotate: what is read as a reference
    /// and what is read as a value. It names no value and no offset (R11.84).
    /// </summary>
    public const string RefusalSentence =
        "If the ConnectionStringPassword hit is a reference to a secret and not the secret, write the reference as a variable in capitals " +
        "(`$DB_PASS`, `${DB_PASS}` or `%DB_PASS%`) or as a placeholder (`<password>`, `{{db_pass}}` or `****`): " +
        "those are not read as credentials, and any other value in that position is.";

    /// <summary>The sentence the write tools' descriptions and <c>curia help</c> carry beside "do not include credentials".</summary>
    public const string DescriptionSentence =
        "A reference to a secret is not a credential: write a variable in capitals as `$DB_PASS`, `${DB_PASS}` or `%DB_PASS%` " +
        "and a placeholder as `<password>`; the value itself is a credential, so never write it.";

    /// <summary>The sentence a credential refusal carries after the instruction to rotate, or <see langword="null"/> where the refusal carries none.</summary>
    public static string? SentenceFor(Error error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return ClientErrors.IsConnectionStringCredentialMaterial(error) ? RefusalSentence : null;
    }
}
