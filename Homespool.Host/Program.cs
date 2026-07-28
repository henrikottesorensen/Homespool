using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

using Homespool.Data;
using Homespool.Host.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Scalar.AspNetCore;
using Serilog;
using Serilog.Events;
using Serilog.Formatting.Compact;

namespace Homespool.Host;

public static class Program
{
    public static void Main(string[] args)
    {
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Information)
            .Enrich.FromLogContext()
            .WriteTo.Console(new RenderedCompactJsonFormatter())
            .CreateBootstrapLogger();

        try
        {
            WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

            // Add services to the container.
            builder.Services.AddSerilog((services, lc) => lc
                   .ReadFrom.Configuration(builder.Configuration)
                   .ReadFrom.Services(services)
                   .Enrich.FromLogContext()
                   .WriteTo.Console(new RenderedCompactJsonFormatter()));

            builder.Services.AddHomespoolData(builder.Configuration);

            builder.Services.AddDatabaseDeveloperPageExceptionFilter();

            builder.Services.AddDataProtection()
                            .PersistKeysToDbContext<HSDbContext>();

            builder.Services.AddAuthentication()
                            .AddPrusaConnectPrinterAuthentication()
                            .AddApiTokenAuthentication();

            builder.Services.AddIdentity<Model.Entities.HSUser, IdentityRole<long>>(options => options.SignIn.RequireConfirmedAccount = true)
                            .AddEntityFrameworkStores<HSDbContext>()
                            .AddClaimsPrincipalFactory<Services.HSUserClaimsPrincipalFactory>()
                            .AddDefaultTokenProviders();

            builder.Services.ConfigureApplicationCookie(options =>
            {
                // Cookie settings
                options.Cookie.HttpOnly = true;
                options.ExpireTimeSpan = TimeSpan.FromDays(1);

                options.LoginPath = "/Account/Login";
                options.AccessDeniedPath = "/Account/AccessDenied";
                options.SlidingExpiration = true;

                // An unauthenticated /api call answered with a redirect to an HTML login page is
                // useless to a script - and arrives as 200. See ApiStatusCodeCookieEvents.
                ApiStatusCodeCookieEvents.Apply(options);
            });

            // Add services to the container.
            builder.Services.AddAuthorization(Authorization.Builder.Build);

            builder.Services.AddRazorPages();

            builder.Services.AddControllers(options =>
                options.Conventions.Add(new ApiExplorerVisibilityConvention()));

            builder.Services.AddOpenApi();

            builder.Services.Configure<PrusaConnect.PrusaConnectOptions>(
                builder.Configuration.GetSection(PrusaConnect.PrusaConnectOptions.SectionName));

            builder.Services.Configure<Services.SmtpOptions>(
                builder.Configuration.GetSection(Services.SmtpOptions.SectionName));

            builder.Services.Configure<Services.InvitationOptions>(
                builder.Configuration.GetSection(Services.InvitationOptions.SectionName));

            Services.SmtpOptions smtpOptions = new();
            builder.Configuration.GetSection(Services.SmtpOptions.SectionName).Bind(smtpOptions);

            // Stateless, so a singleton is fine; it exists purely so tests can substitute a fake transport.
            builder.Services.AddSingleton<Services.ISmtpTransportFactory, Services.MailKitSmtpTransportFactory>();

            // Which sender is registered is decided by configuration alone, never by probing the network, so that a
            // mail server being down cannot quietly change how accounts are created. See SmtpOptions.IsConfigured.
            if (smtpOptions.IsConfigured)
            {
                builder.Services.AddScoped<Services.IEmailSender, Services.SmtpEmailSender>();

                // Only with a mail server to send through - otherwise this is a background service
                // whose whole job is to log that it cannot do its job. The banner and /health cover
                // deployments without SMTP.
                builder.Services.AddHostedService<Services.TelemetryAlertService>();
            }
            else
            {
                builder.Services.AddScoped<Services.IEmailSender, Services.LoggingEmailSender>();
            }

            builder.Services.AddHostedService<Services.SmtpConnectivityProbe>();

            // Resolves the "confirm accounts at creation" rule once from SmtpOptions, so account-creation
            // pages inject this instead of SmtpOptions. Singleton: SMTP config is fixed at startup.
            builder.Services.AddSingleton<Services.AccountConfirmationPolicy>();

            // Holds the first-run bootstrap secret and the one-way "an admin exists" flag; seeded once
            // by SeedAdminBootstrap after migration. Singleton so the flag is process-wide.
            builder.Services.AddSingleton<Services.SetupState>();

            // Factory-activated (IMiddleware) so it is resolved from the container. Singleton: it holds
            // no per-request state, only the singleton SetupState.
            builder.Services.AddSingleton<Services.SetupGateMiddleware>();

            builder.Services.AddScoped<PrusaConnect.PrusaConnectService>()
                            .AddScoped<PrusaConnect.WebSocketHandler>()
                            .AddScoped<PrusaConnect.TokenService>()
                            .AddScoped<PrusaConnect.CodeGenerator>()
                            .AddScoped<PrusaConnect.MessageDispatcher>()
                            .AddScoped<PrusaConnect.PrinterCommandService>();

            // Plain singletons, not TelemetryWriter's singleton-with-IServiceScopeFactory pattern below:
            // neither touches HSDbContext, only in-memory state (the directory of live connection
            // actors, and the actors' own singleton dependencies), so there is no scoped dependency
            // to protect against capturing. The actors themselves are not registered at all - one is
            // created per accepted WebSocket by the factory and lives exactly as long as that request.
            builder.Services.AddSingleton<PrusaConnect.PrinterConnectionRegistry>();
            builder.Services.AddSingleton<PrusaConnect.PrinterConnectionActorFactory>();

            // Singleton because its whole value is accumulating across connections and printers:
            // "this firmware sends a field we do not model" is a fact about the deployment, and a
            // per-request instance would forget it between messages.
            builder.Services.AddSingleton<PrusaConnect.UnknownFieldTracker>();

            // One store, two faces: actors resolve hashes through ITransferContentStore, request
            // handlers register files through ITransferOffers. Singleton because an offer has to
            // outlive the request that made it - the printer collects it on its own schedule.
            builder.Services.AddSingleton<PrusaConnect.Transfers.TransferOfferStore>();
            builder.Services.AddSingleton<PrusaConnect.Transfers.ITransferContentStore>(
                sp => sp.GetRequiredService<PrusaConnect.Transfers.TransferOfferStore>());
            builder.Services.AddSingleton<PrusaConnect.Transfers.ITransferOffers>(
                sp => sp.GetRequiredService<PrusaConnect.Transfers.TransferOfferStore>());

            // Uploaded gcode: options, the store, and the content-root accessor it needs. Singleton
            // because the store holds no per-request state - it is a path and a couple of rules.
            builder.Services.Configure<PrusaConnect.Transfers.FileStorageOptions>(
                builder.Configuration.GetSection(PrusaConnect.Transfers.FileStorageOptions.SectionName));
            builder.Services.AddSingleton<PrusaConnect.Transfers.IHostEnvironmentAccessor>(
                sp => new PrusaConnect.Transfers.HostEnvironmentAccessor(sp.GetRequiredService<IWebHostEnvironment>().ContentRootPath));
            builder.Services.AddSingleton<PrusaConnect.Transfers.UploadedFileStore>();

            AddPrinterEndpointRateLimiting(builder);

            // Scoped, following the WebSocketHandler it runs: one session per accepted upgrade.
            builder.Services.AddScoped<PrusaConnect.PrinterConnectionSession>();

            // Singleton, not scoped like its neighbors above: one drain loop and one in-memory
            // live-state cache for the whole process, fed by every request's scoped
            // MessageDispatcher through the ITelemetrySink interface - so a request never hands
            // the writer its own HSDbContext, only a DTO.
            //
            // The writer still needs HSDbContext to persist, which is the usual trap for a
            // singleton: inject the scoped context directly and it gets captured once, reused
            // forever, single-threaded and stale, for the life of the process. TelemetryWriter
            // avoids this by injecting IServiceScopeFactory instead - itself a singleton, safe to
            // hold - and calling CreateScope() fresh in HydrateAsync and FlushAsync, each wrapped
            // in a `using` that disposes the scope (and its HSDbContext) the moment that one
            // read or write finishes. No HSDbContext field ever exists on TelemetryWriter itself.
            builder.Services.AddSingleton<PrusaConnect.TelemetryWriter>();
            builder.Services.AddSingleton<PrusaConnect.ITelemetrySink>(sp => sp.GetRequiredService<PrusaConnect.TelemetryWriter>());
            builder.Services.AddSingleton<PrusaConnect.ITelemetryHealthSource>(sp => sp.GetRequiredService<PrusaConnect.TelemetryWriter>());
            builder.Services.AddHostedService(sp => sp.GetRequiredService<PrusaConnect.TelemetryWriter>());

            // The process answering requests says nothing about whether it is still recording
            // anything - a flush bug once made every write fail permanently while the service looked
            // entirely healthy from outside. This is the hook a monitoring system can watch.
            // Tagged, because the two endpoints below must not report the same thing. Only checks
            // tagged "live" answer /health/live, and only a fault a restart would fix may carry that
            // tag - see TelemetryWriterLivenessHealthCheck.
            builder.Services.AddHealthChecks()
                   .AddCheck<PrusaConnect.TelemetryPersistenceHealthCheck>("telemetry-persistence")
                   .AddCheck<PrusaConnect.TelemetryWriterLivenessHealthCheck>("telemetry-writer-alive", tags: [LivenessTag]);

            // Sweeps TelemetrySample rows past StorageOptions.TelemetryRetentionDays. No interface
            // registration needed, unlike TelemetryWriter above - nothing else ever needs to reach it.
            builder.Services.AddHostedService<Services.TelemetryRetentionService>();

            // Scoped, unlike their singleton neighbors above, because they hold the scoped HSDbContext.
            builder.Services.AddScoped<Services.TeamService>();
            builder.Services.AddScoped<Services.UnitOfWork>();
            builder.Services.AddScoped<Services.InvitationService>();
            builder.Services.AddScoped<Services.PrinterQueryService>();
            builder.Services.AddScoped<Services.ApiTokenService>();

            WebApplication app = builder.Build();

            // Ctrl-C/SIGTERM is otherwise silent: the framework's own "Application is shutting
            // down..." comes from Microsoft.Hosting.Lifetime, and Serilog's Microsoft override
            // (appsettings.json) filters that namespace to Warning. An operator watching a blank
            // console while telemetry drains has no way to tell progress from a hang, and reaches
            // for SIGKILL - which is exactly what loses the buffered samples. TelemetryWriter logs
            // the matching "drained" or "unwritten" line when it finishes.
            app.Lifetime.ApplicationStopping.Register(() =>
                app.Logger.LogInformation("Shutting down: draining buffered telemetry to the database. Please let this finish."));

            // Apply migrations on service startup. (assuming StorageOptions have enabled it).
            app.Services.MigrateHomespoolData();

            // Ensure the admin role exists and, if no administrator has been created yet, mint and log
            // the one-time /setup token. Runs inline so setup state is settled before the first request.
            Services.AdminBootstrap.SeedAdminBootstrap(app.Services);

            // Configure the HTTP request pipeline.
            if (app.Environment.IsDevelopment())
            {
                app.MapOpenApi();
                app.MapScalarApiReference();
            }

            // Log HTTP requests with Serilog, order of this matters.
            // Requests handled before in the pipeline are NOT logged.
            app.UseSerilogRequestLogging();

            // Everything except /health. A probe runs inside the container over plain HTTP, and a
            // 307 to https is not a failure to curl - so with redirection applied, a monitoring
            // check would report success without ever reaching the health endpoint. Excluding the
            // path keeps the probe honest wherever TLS is terminated.
            app.UseWhen(
                context => !context.Request.Path.StartsWithSegments(HealthEndpointPath, StringComparison.OrdinalIgnoreCase),
                branch => branch.UseHttpsRedirection());

            app.UseRouting();

            // Before an administrator exists, funnel every navigable page to /setup. No-op once setup
            // completes. Placed after routing so static-asset and printer endpoints resolve normally.
            app.UseMiddleware<Services.SetupGateMiddleware>();

            // After UseRouting, so the endpoint's [EnableRateLimiting] metadata is resolved, and
            // before authentication, so a rejected request costs no database work.
            app.UseRateLimiter();

            app.UseAuthentication();
            app.UseAuthorization();

            // Anonymous by design: a monitoring system holds no credentials, and the response carries
            // only counters and timestamps about this service's own write path - nothing about
            // printers, jobs or users.
            //
            // Everything, for monitoring and for humans. Alert on this; never restart on it.
            app.MapHealthChecks(HealthEndpointPath, new HealthCheckOptions
            {
                ResponseWriter = WriteHealthResponseAsync,
            });

            // Liveness, and the safe target for anything that can kill the container: a Kubernetes
            // livenessProbe, a Swarm healthcheck, an autoheal sidecar. Reports only faults a restart
            // fixes, so a rejecting database can never trigger a restart loop that discards the
            // buffered telemetry with every cycle.
            //
            // Also the right target for a startupProbe: migrations and admin bootstrap run before
            // app.Run(), so Kestrel is not accepting connections until they finish - any successful
            // response already means startup completed, and no separate endpoint is needed. And for a
            // readinessProbe, since a degraded writer is a reason to alert, not a reason to stop
            // accepting printer connections.
            app.MapHealthChecks($"{HealthEndpointPath}/live", new HealthCheckOptions
            {
                Predicate = registration => registration.Tags.Contains(LivenessTag),
                ResponseWriter = WriteHealthResponseAsync,
            });

            app.MapControllers();

            app.MapStaticAssets();
            app.MapRazorPages()
               .WithStaticAssets();

            app.UseWebSockets(new WebSocketOptions()
            {
                KeepAliveInterval = TimeSpan.FromSeconds(120),
            });

            app.Run();
        }
        catch (HostAbortedException)
        {
            // Thrown by design-time tooling (dotnet-ef) after it has built the service provider.
            // Not a failure, and logging it as Fatal makes every migration command look broken.
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Application terminated unexpectedly");
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }

    /// <summary>Where the health endpoints live. Shared so the HTTPS-redirection exclusion and the
    /// setup gate's allowance cannot drift away from the routes themselves. <c>/health/live</c> sits
    /// underneath, so both are covered by one path prefix.</summary>
    private const string HealthEndpointPath = "/health";

    /// <summary>Marks a check as safe for a liveness probe - that is, one whose failure a restart
    /// would actually fix.</summary>
    private const string LivenessTag = "live";

    /// <summary>
    /// Rate-limit policy for the two anonymous <c>/p/register</c> actions. Named here so the policy
    /// and the <c>[EnableRateLimiting]</c> attributes on the controller cannot drift apart.
    /// </summary>
    internal const string PrinterRegistrationRateLimitPolicy = "printer-registration";

    /// <summary>Rate-limit policy for the <c>/p/ws</c> upgrade.</summary>
    internal const string PrinterSocketRateLimitPolicy = "printer-socket";

    /// <summary>
    /// Caps how fast the anonymous printer endpoints can be hit. These are the only routes an
    /// unauthenticated caller on the internet can reach, and both cost something: <c>POST
    /// /p/register</c> creates or renews a database row per call, and <c>GET /p/register</c> is a
    /// guessing oracle for a pending registration code.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Assume the deployment is internet-facing.</b> People expose self-hosted printer servers
    /// however firmly the documentation advises otherwise - OctoPrint's mass exposure is the
    /// precedent - so "it is only on a LAN" is not a security property this project can rely on.
    /// </para>
    /// <para>
    /// <b>Deliberately global, not partitioned per client IP.</b> The documented way to expose this
    /// service is behind a reverse proxy (<c>PUBLIC_HOST</c>/<c>PUBLIC_TLS</c>), and nothing here
    /// calls <c>UseForwardedHeaders</c> - so every request's <c>RemoteIpAddress</c> would be the
    /// proxy's. Partitioning on that puts every printer and every attacker in one bucket, meaning the
    /// first brute-force attempt locks out the household: strictly worse than no limiting at all.
    /// Honouring <c>X-Forwarded-For</c> is its own piece of work (it needs
    /// <c>KnownProxies</c>/<c>KnownNetworks</c>, or an attacker simply rotates the header for
    /// unlimited buckets), and per-IP limits should wait for it.
    /// </para>
    /// <para>
    /// <b>Limits are generous on purpose, because rejecting a real printer is expensive.</b> The
    /// firmware treats any non-2xx from <c>/p/register</c> as <c>OnlineError::Server</c> and burns one
    /// of only three POST retries before abandoning registration permanently (registrator.hpp,
    /// <c>starting_retries = 3</c>); a rejected poll is milder but still noise. A healthy printer
    /// POSTs about once in its life and polls every 5s (≈12/min), so ten printers sit near 120/min
    /// against the 300/min ceiling here, while an attacker is bounded to ~430k attempts/day instead of
    /// unbounded. That is not the whole answer for the code-guessing surface - a per-registration
    /// attempt cap is (see notes/claim-code-usability.md) - but it turns "unlimited" into "bounded".
    /// </para>
    /// <para>
    /// The login form is <em>not</em> rate-limited here, and deliberately so: Identity's account
    /// lockout now bounds password guessing per account (see <c>Login.cshtml.cs</c>), which is both
    /// proxy-agnostic and impossible to evade by rotating source addresses. A global limiter on login
    /// would instead let one attacker lock out every legitimate user at once.
    /// </para>
    /// </remarks>
    private static void AddPrinterEndpointRateLimiting(WebApplicationBuilder builder)
    {
        builder.Services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            options.AddFixedWindowLimiter(PrinterRegistrationRateLimitPolicy, limiter =>
            {
                limiter.PermitLimit = 300;
                limiter.Window = TimeSpan.FromMinutes(1);
                limiter.QueueLimit = 0;
            });

            options.AddFixedWindowLimiter(PrinterSocketRateLimitPolicy, limiter =>
            {
                // A printer holding a stale token retries roughly once a minute (observed in
                // notes/cross-channel-identity-bug.md), so this is ample for a fleet while still
                // bounding an attacker probing tokens.
                limiter.PermitLimit = 120;
                limiter.Window = TimeSpan.FromMinutes(1);
                limiter.QueueLimit = 0;
            });
        });
    }

    /// <summary>
    /// Writes the health report as JSON rather than the default bare status word.
    /// </summary>
    /// <remarks>
    /// The status code is what a monitoring system alerts on - Healthy and Degraded are 200,
    /// Unhealthy is 503 - but the body is what tells whoever gets paged which of the two very
    /// different problems they have: a database that is briefly stuck, or one that has been stuck
    /// long enough to lose events for good.
    /// </remarks>
    private static Task WriteHealthResponseAsync(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json";

        return context.Response.WriteAsync(JsonSerializer.Serialize(new
        {
            status = report.Status.ToString(),
            totalDurationMs = report.TotalDuration.TotalMilliseconds,
            checks = report.Entries.Select(entry => new
            {
                name = entry.Key,
                status = entry.Value.Status.ToString(),
                description = entry.Value.Description,
                data = entry.Value.Data,
            }),
        }));
    }
}
