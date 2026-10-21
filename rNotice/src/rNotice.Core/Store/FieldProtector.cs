using System.Security.Cryptography;
using System.Text;

namespace rNotice.Core.Store;

/// <summary>Encrypts the store's content columns. Swappable so tests can see what's written.</summary>
public interface IFieldProtector
{
    byte[] Protect(string plaintext);
    string Unprotect(byte[] ciphertext);
}

/// <summary>
/// DPAPI, scoped to the current Windows user. No key for us to manage or lose: decryption is
/// tied to the account. Protects the file lifted off a backup or a stolen disk, or read by
/// another account on the machine — not someone already in your logged-on session.
/// </summary>
public sealed class DpapiFieldProtector : IFieldProtector
{
    // Not a secret. It stops another app's DPAPI blobs from decrypting as ours, and the reverse.
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("rNotice.Store.v1");

    public byte[] Protect(string plaintext) =>
        ProtectedData.Protect(Encoding.UTF8.GetBytes(plaintext), Entropy, DataProtectionScope.CurrentUser);

    public string Unprotect(byte[] ciphertext) =>
        Encoding.UTF8.GetString(ProtectedData.Unprotect(ciphertext, Entropy, DataProtectionScope.CurrentUser));
}
