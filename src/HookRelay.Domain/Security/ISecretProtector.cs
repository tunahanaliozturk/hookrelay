namespace HookRelay.Domain.Security;

/// <summary>
/// Two-way protection for signing secrets at rest.
/// </summary>
/// <remarks>
/// Inbound credentials get hashed, because verification only ever needs to compare. A webhook sender is
/// the other side of that relationship: it has to reproduce the secret to sign with it, so a one-way hash
/// is not an option and encryption is the honest answer. The trade-off and its blast radius are written up
/// in <c>docs/adr/0003-encrypt-signing-secrets-rather-than-hash-them.md</c>.
/// </remarks>
public interface ISecretProtector
{
    /// <summary>Encrypts a secret for storage.</summary>
    /// <param name="plaintext">The raw signing secret.</param>
    string Protect(string plaintext);

    /// <summary>Decrypts a stored secret so a delivery can be signed with it.</summary>
    /// <param name="protectedValue">A value previously returned by <see cref="Protect"/>.</param>
    string Unprotect(string protectedValue);
}
