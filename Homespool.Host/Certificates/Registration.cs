using System;
using System.IO;
using System.Security.Cryptography.X509Certificates;

using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using Homespool.Data;

namespace Homespool.Host.Certificates;

/// <summary>
/// Wires up Data Protection, so <c>Program.cs</c> says what is being added rather than how.
/// </summary>
/// <remarks>
/// <c>Cameras/Registration.cs</c> is the same idea: the configuration for a thing lives beside the
/// thing.
/// </remarks>
public static class Registration
{
    /// <summary>
    /// Adds Data Protection, persisting the key ring to the database and encrypting it with a
    /// certificate on disk.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This one touches the filesystem, which a registration method normally would not.</b> It
    /// mints the key-protection certificate if there is not one already, because the key ring has to
    /// be encrypted from the first key the application ever mints — waiting for the container to be
    /// built would mean the first key is written in the clear, and Data Protection never rewrites a
    /// key once stored. The alternative, leaving the minting in <c>Program.cs</c>, splits one
    /// decision across two files and leaves the more surprising half in the noisier place.
    /// </para>
    /// <para>
    /// Configuration and the environment are passed rather than resolved, for the same reason: there
    /// is no service provider yet. <see cref="CertificateOptions"/> is bound here by hand and
    /// registered for injection separately in <c>Program.cs</c>, which is not duplication so much as
    /// the same section read at two different times.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddHomespoolDataProtection(this IServiceCollection services,
                                                                IConfiguration configuration,
                                                                IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);

        CertificateOptions options = configuration.GetSection(CertificateOptions.SectionName)
                                                  .Get<CertificateOptions>() ?? new CertificateOptions();

        // KeyProtectionDirectory exists for the test host, which cannot redirect this resolution
        // any other way - the option's remarks say why. Unset, the certificate sits beside the
        // authority.
        string configured = options.KeyProtectionDirectory ?? options.Directory;

        string directory = Path.IsPathRooted(configured) ?
            configured :
            Path.Combine(environment.ContentRootPath, configured);

        // Under the authority's passphrase, deliberately - the two keys share every case in which a
        // passphrase helps, so a second secret would be one more thing to lose for no change in what
        // a copied volume yields. DataProtectionCertificate's remarks carry the argument.
        X509Certificate2 keyProtection = DataProtectionCertificate.Ensure(
            directory, options.KeyProtectionValidityDays, options.AuthorityPassphrase, TimeProvider.System);

        services.AddDataProtection()
                .PersistKeysToDbContext<HomespoolDbContext>()
                .ProtectKeysWithCertificate(keyProtection)
                .UnprotectKeysWithAnyCertificate(keyProtection);

        return services;
    }
}
