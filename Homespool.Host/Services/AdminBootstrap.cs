using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Homespool.Model.Entities;

namespace Homespool.Host.Services;

/// <summary>
/// First-run administrator bootstrap: ensures the <see cref="AdminRole"/> exists and, when no
/// administrator has been created yet, mints the one-time <c>/setup</c> token and logs it for the
/// operator.
/// </summary>
public static class AdminBootstrap
{
    /// <summary>
    /// The single privileged role. Ordinary users hold no role (AGENT-NOTES phase-1.5 decision 6).
    /// </summary>
    public const string AdminRole = "Admin";

    /// <summary>
    /// Runs once at startup, after <see cref="Data.DataServiceCollectionExtensions.MigrateHomespoolData"/>
    /// so the Identity tables exist. Seeds the admin role, then seeds <see cref="SetupState"/> from
    /// whether an admin account is present.
    /// </summary>
    /// <remarks>
    /// Run inline (blocking) rather than as an <see cref="Microsoft.Extensions.Hosting.IHostedService"/>
    /// on purpose: the setup-complete flag and the bootstrap token must be settled before the first
    /// request can reach <c>/setup</c>. A hosted service runs after the server starts accepting
    /// connections and would open exactly that window.
    /// </remarks>
    [SuppressMessage("Usage", "VSTHRD002:Avoid problematic synchronous waits",
                     Justification =
                         "Deliberately synchronous: the setup flag and bootstrap token must be settled before the first request can reach /setup, which a hosted service would not guarantee.")]
    public static void SeedAdminBootstrap(this IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);

        SeedAsync(services).GetAwaiter().GetResult();
    }

    private static async Task SeedAsync(IServiceProvider services)
    {
        using IServiceScope scope = services.CreateScope();

        ILogger logger = scope.ServiceProvider
                              .GetRequiredService<ILoggerFactory>()
                              .CreateLogger(typeof(AdminBootstrap).FullName!);

        RoleManager<IdentityRole<long>> roleManager =
            scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<long>>>();

        if (!await roleManager.RoleExistsAsync(AdminRole))
        {
            IdentityResult roleResult = await roleManager.CreateAsync(new IdentityRole<long>(AdminRole));

            if (!roleResult.Succeeded)
            {
                // Nothing below can work without the role, and a half-seeded database is worse than a
                // loud failure, so refuse to start rather than limp on.
                throw new InvalidOperationException(
                    $"Could not create the '{AdminRole}' role: {DescribeErrors(roleResult)}");
            }

            logger.LogInformation("Created the {AdminRole} role.", AdminRole);
        }

        UserManager<HSUser> userManager = scope.ServiceProvider.GetRequiredService<UserManager<HSUser>>();
        SetupState setupState = scope.ServiceProvider.GetRequiredService<SetupState>();

        IList<HSUser> admins = await userManager.GetUsersInRoleAsync(AdminRole);
        string? token = setupState.Initialize(admins.Count > 0);

        if (token is null)
        {
            logger.LogInformation(
                "An administrator account already exists; first-time setup is disabled.");

            return;
        }

        // The console/log is the intended delivery channel for this credential (decision 2): it is
        // one-time, ephemeral and never persisted, and printing it is the whole point. This is not the
        // same as leaking a durable secret into logs.
        logger.LogWarning(
            "No administrator account exists yet. Complete first-time setup by opening /setup and " +
            "entering this one-time token:{NewLine}{NewLine}    {SetupToken}{NewLine}{NewLine}" +
            "The token is held in memory only and is regenerated on every restart; it is never written to disk.",
            Environment.NewLine, Environment.NewLine, token, Environment.NewLine, Environment.NewLine);
    }

    private static string DescribeErrors(IdentityResult result)
    {
        return string.Join("; ", System.Linq.Enumerable.Select(result.Errors, e => e.Description));
    }
}
