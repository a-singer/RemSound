using System.Security.Cryptography;
using RemSound.Core;

namespace RemSound.Receiver;

/// <summary>
/// Decrypts incoming audio payloads with the key derived from the local profile's password.
/// One instance is shared by every <see cref="StreamSession"/>, which is safe because all
/// receive-side decode work runs on the single network-listener thread (see StreamSession's
/// class summary). The cipher is rebuilt only when the key reference changes (a password
/// change), and a single reusable scratch buffer avoids per-packet allocation on the hot path.
/// 2026-05-31.
/// </summary>
internal sealed class AudioDecryptor : IDisposable
{
    private AesGcm? gcm;
    private byte[]? keyCached;
    // Sized for the largest decrypted frame (Opus 20 ms / PCM 5 ms are well under this).
    private readonly byte[] scratch = new byte[8192];

    /// <summary>True once a password/key is set — without one, nothing can be decrypted and all
    /// audio is dropped (encryption is mandatory).</summary>
    public bool HasKey => gcm is not null;

    /// <summary>Rebuild the cipher if the key reference changed. Call on the network thread
    /// before decrypting. Pushing a new array (not mutating in place) is what signals a change.</summary>
    public void EnsureKey(byte[]? key)
    {
        if (ReferenceEquals(key, keyCached)) return;
        gcm?.Dispose();
        gcm = key is null ? null : RemSoundCrypto.CreateGcm(key);
        keyCached = key;
    }

    /// <summary>Decrypt a ciphertext payload into the shared scratch. Returns the plaintext span
    /// (a view into the scratch, valid until the next call) or an empty span on failure — wrong
    /// key (password mismatch), tampered packet, or no key set. Single-threaded use only.</summary>
    public ReadOnlySpan<byte> TryDecrypt(ReadOnlySpan<byte> ciphertext)
    {
        if (gcm is null) return default;
        return RemSoundCrypto.TryDecryptInto(gcm, ciphertext, scratch, out var len)
            ? scratch.AsSpan(0, len)
            : default;
    }

    public void Dispose()
    {
        gcm?.Dispose();
        gcm = null;
        keyCached = null;
    }
}
