using System;

using Microsoft.Extensions.Options;

using Homespool.Host.Configuration;

namespace Homespool.Host.Mail;

/// <summary>
/// Decrypts the stored SMTP password after binding, so that nothing else has to know it was stored
/// encrypted.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the seam the whole design rests on.</b> <c>SmtpEmailSender</c>, the connectivity probe
/// and the tests all take <c>IOptions&lt;SmtpOptions&gt;</c> and read
/// <see cref="SmtpOptions.Password"/>; none of them mentions Data Protection, and none of them has to
/// change when the storage does. A post-configure runs after every binding source has had its say,
/// which is exactly when the ciphertext is known and before any consumer resolves the options.
/// </para>
/// <para>
/// <b>An undecryptable value leaves the password empty rather than stopping the application.</b> The
/// protector logs it. What follows is an authentication failure against the mail server, which is
/// visible, survivable and fixed by entering the password again - where throwing here would take out
/// a deployment that is otherwise fine, over a setting most deployments do not use at all.
/// </para>
/// </remarks>
public sealed class SmtpPasswordUnprotector : IPostConfigureOptions<SmtpOptions>
{
    private readonly SettingsSecretProtector _protector;

    /// <summary>Creates the post-configure step.</summary>
    /// <param name="protector">Reads the stored ciphertext back.</param>
    public SmtpPasswordUnprotector(SettingsSecretProtector protector)
    {
        ArgumentNullException.ThrowIfNull(protector);

        _protector = protector;
    }

    /// <inheritdoc />
    public void PostConfigure(string? name, SmtpOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrEmpty(options.ProtectedPassword))
        {
            // Nothing stored encrypted. Whatever Password already holds arrived from the environment,
            // the migration one-shot or a hand edit, and is used as it stands.
            return;
        }

        options.Password = _protector.Reveal(
            options.ProtectedPassword,
            $"{SmtpOptions.SectionName}:{nameof(SmtpOptions.ProtectedPassword)}") ?? string.Empty;
    }
}
