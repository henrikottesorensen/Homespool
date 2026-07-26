using System;
using System.Security.Cryptography;
using System.Threading;

namespace Homespool.Host.Services;

/// <summary>
/// Holds the first-run bootstrap secret and the one-way "an administrator exists" flag for the
/// <c>/setup</c> page.
/// </summary>
/// <remarks>
/// <para>
/// Registered as a singleton and seeded once at startup by <see cref="AdminBootstrap"/>. The secret
/// is a 24-byte CSPRNG value kept <b>in memory only</b> - never persisted - so a restart mints a
/// fresh one and nothing sensitive lands on disk (see AGENT-NOTES phase-1.5 decision 2). At
/// one-container scale that is the whole story; two replicas would each print their own token, which
/// is the reason not to do this if that ever changes.
/// </para>
/// <para>
/// It is deliberately <i>not</i> hashed the way <see cref="PrusaConnect.TokenService"/> hashes
/// printer tokens. That service salts and runs PBKDF2 because those tokens live in the database; this
/// secret never leaves process memory, where an attacker able to read it can read the derived key
/// just as easily. What still matters against a network caller is a constant-time comparison, which
/// <see cref="Verify"/> does with <see cref="CryptographicOperations.FixedTimeEquals"/>.
/// </para>
/// </remarks>
public sealed class SetupState
{
    /// <summary>
    /// Size of the bootstrap secret. 24 bytes is 192 bits of entropy - not brute-forceable - and
    /// encodes to a clean 32-character base64 string with no padding.
    /// </summary>
    private const int SecretBytes = 24;

    private readonly Lock _gate = new();

    private byte[]? _secret;
    private bool _isComplete;

    /// <summary>
    /// True once an administrator account exists. A one-way transition: only the completed state is
    /// cached, because "no admin yet" can become "admin exists" but never the reverse within a
    /// process.
    /// </summary>
    public bool IsComplete
    {
        get
        {
            lock (_gate)
            {
                return _isComplete;
            }
        }
    }

    /// <summary>
    /// Called exactly once at startup. When an admin already exists, marks setup complete and holds
    /// no secret. Otherwise generates the bootstrap secret and returns it base64-encoded, for the
    /// caller to log to the operator - the only time the plaintext is ever exposed.
    /// </summary>
    /// <returns>The base64 secret to surface to the operator, or <c>null</c> when setup is complete.</returns>
    public string? Initialize(bool adminExists)
    {
        lock (_gate)
        {
            if (adminExists)
            {
                _isComplete = true;
                _secret = null;

                return null;
            }

            _secret = RandomNumberGenerator.GetBytes(SecretBytes);
            _isComplete = false;

            return Convert.ToBase64String(_secret);
        }
    }

    /// <summary>
    /// Constant-time check of a candidate token against the bootstrap secret. Returns false once
    /// setup is complete, before initialization, or for a candidate that is absent or not valid
    /// base64 - so a malformed token is rejected exactly like a wrong one.
    /// </summary>
    public bool Verify(string? candidate)
    {
        lock (_gate)
        {
            if (_isComplete || _secret is null || string.IsNullOrEmpty(candidate))
            {
                return false;
            }

            byte[] provided;

            try
            {
                provided = Convert.FromBase64String(candidate);
            }
            catch (FormatException)
            {
                return false;
            }

            // FixedTimeEquals returns false for a length mismatch without an early-out, and the length
            // here (32 base64 chars) is fixed and public anyway, so no timing signal is leaked.
            return CryptographicOperations.FixedTimeEquals(provided, _secret);
        }
    }

    /// <summary>
    /// Marks setup complete and drops the secret. Idempotent; safe to call under a race where two
    /// requests both passed <see cref="Verify"/> before either committed.
    /// </summary>
    public void MarkComplete()
    {
        lock (_gate)
        {
            _isComplete = true;
            _secret = null;
        }
    }
}
