using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Homespool.Model.Entities;

namespace Homespool.Host.Accounts;

/// <summary>
/// First-run administrator bootstrap: ensures the <see cref="AdminRole"/> exists and, when no
/// administrator has been created yet, mints the one-time <c>/setup</c> token and logs it for the
/// operator.
/// </summary>
public static class AdminBootstrap
{
    /// <summary>
    /// The single privileged role. Ordinary users hold no role.
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

        // A restart mints a new token but does not truncate the log, so an operator running
        // `docker logs` sees every token this container has ever printed and only the last is live.
        // Stamping the line with the process start time makes it self-identifying: it matches what
        // `docker inspect --format '{{.State.StartedAt}}'` reports, so the current line can be picked
        // out without correlating timestamps by hand.
        using Process current = Process.GetCurrentProcess();
        DateTime startedAt = current.StartTime.ToUniversalTime();

        // The console/log is the intended delivery channel for this credential (decision 2): it is
        // one-time, ephemeral and never persisted, and printing it is the whole point. This is not the
        // same as leaking a durable secret into logs.
        //
        // One line, the token inline and unquoted (:l). Line breaks cannot be carried in a property:
        // PrintableLogEnricher replaces every control character in one with U+FFFD, so a NewLine
        // property renders as a quoted replacement character.
        logger.LogWarning(
            "No administrator account exists yet. Complete first-time setup by opening /setup and " +
            "entering this one-time token: {SetupToken:l} - it is held in memory only, regenerated on " +
            "every restart and never written to disk. This one belongs to the service that started at " +
            "{StartedAt:O}; disregard any token logged before then.",
            token, startedAt);

        WritePlainly(token, startedAt);
    }

    /// <summary>
    /// The same token, written for a person reading <c>docker compose logs</c>.
    /// </summary>
    /// <remarks>
    /// The console sink writes compact JSON, one event per line, so the logged message reaches the
    /// operator as a long line in a JSON string. The token is read by eye and typed or pasted into a
    /// form, so it is also written here on a line of its own, with real line breaks, as the migration
    /// guard writes its refusal. The logged event stays the record a machine or a test reads.
    /// </remarks>
    private static void WritePlainly(string token, DateTime startedAt)
    {
        Console.Error.WriteLine();
        Console.Error.WriteLine("No administrator account exists yet. To finish setting up, open /setup and enter this one-time token:");
        Console.Error.WriteLine();
        Console.Error.WriteLine($"    {token}");
        Console.Error.WriteLine();
        Console.Error.WriteLine(
            "It is held in memory only and replaced on every restart; this one belongs to the service that started at " +
            startedAt.ToString("O", CultureInfo.InvariantCulture) + ".");
        Console.Error.WriteLine();
    }

    private static string DescribeErrors(IdentityResult result)
    {
        return string.Join("; ", System.Linq.Enumerable.Select(result.Errors, e => e.Description));
    }
}
