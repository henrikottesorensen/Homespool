using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Net.Mime;
using System.Text.Json;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Builder;
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
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;

using Scalar.AspNetCore;

using Serilog;
using Serilog.Events;
using Serilog.Formatting.Compact;

using Homespool.Data;
using Homespool.Host.Authentication;
using Homespool.Host.Cameras;
using Homespool.Host.Certificates;
using Homespool.Host.Configuration;
using Homespool.Host.Listeners;
using Homespool.Host.Localisation;
using Homespool.Host.Queue;

namespace Homespool.Host;

public static class Program
{
    /// <summary>The argument that turns this into a one-shot time zone conversion rather than a server.</summary>
    /// <remarks>
    /// <para>
    /// <b>Why the server carries this at all.</b> On Windows the wizard runs inside this image, and the
    /// zone Windows reports - <c>W. Europe Standard Time</c> - is not an IANA name, which is what
    /// <c>TZ</c> takes. The conversion is one framework call, and this container is the only .NET in
    /// reach: Windows PowerShell 5.1 is .NET Framework 4.8, where
    /// <c>TryConvertWindowsIdToIanaId</c> does not exist, and a Windows machine that cannot install
    /// Docker Desktop has no other runtime either.
    /// </para>
    /// <para>
    /// The alternative was shipping a mapping table in a shell script, which would be wrong the first
    /// time a zone changed and would have to be maintained by hand against data ICU already has.
    /// </para>
    /// </remarks>
    private const string IanaTimeZoneArgument = "--iana-timezone";

    /// <summary>
    /// Prints the IANA name for a Windows time zone, or nothing when there is no answer.
    /// </summary>
    /// <remarks>
    /// The optional second argument is a two-letter region, and it earns its place: <c>Romance
    /// Standard Time</c> alone maps to <c>Europe/Paris</c>, and with <c>DK</c> to
    /// <c>Europe/Copenhagen</c>. The two behave identically - same offset, same rules - but a Dane
    /// reading Europe/Paris in their own <c>.env</c> would reasonably think it was a mistake.
    /// </remarks>
    /// <param name="args">The argument, the Windows zone identifier, and optionally a region.</param>
    /// <returns>Zero when a name was written, one when the zone was not recognised.</returns>
    private static int WriteIanaTimeZone(string[] args)
    {
        if (args.Length < 2)
        {
            return 1;
        }

        string windowsId = args[1];
        string? region = args.Length > 2 && !string.IsNullOrWhiteSpace(args[2]) ? args[2] : null;

        bool converted = region is null ?
            TimeZoneInfo.TryConvertWindowsIdToIanaId(windowsId, out string? iana) :
            TimeZoneInfo.TryConvertWindowsIdToIanaId(windowsId, region, out iana);

        if (!converted || string.IsNullOrEmpty(iana))
        {
            return 1;
        }

        Console.WriteLine(iana);
        return 0;
    }

    public static void Main(string[] args)
    {
        // Answered before anything else starts, because it is not a server run at all: setup-env.sh
        // asks this to turn a Windows time zone into an IANA one and exits.
        if (args.Length > 0 && args[0] == IanaTimeZoneArgument)
        {
            Environment.ExitCode = WriteIanaTimeZone(args);
            return;
        }

        // Which build this is, and equally not a server run. Answered here rather than after the
        // logger for the same reason as the applet above: it must work on a deployment that cannot
        // start at all - no database mounted, a broken .env - because "what is actually running?" is
        // asked precisely when something is wrong. It is also the answer the AGPL's offer of source
        // needs, so it has to hold on an image nobody can log in to.
        //
        // Note what an OLDER image does with this argument, which is documented at length in
        // setup-env.sh: it does not recognise it, hands it to WebApplication.CreateBuilder, STARTS
        // THE SERVER and prints a page of JSON. Anything that ever scripts this must bound it in
        // time and check that the output looks like an answer, rather than trusting it.
        if (args.Length > 0 && args[0] == BuildInformation.VersionArgument)
        {
            Environment.ExitCode = BuildInformation.WriteVersion("Homespool");
            return;
        }

        // The schema this build expects, written to a file so a deployed database can be compared
        // against it. Also not a server run, and answered here for the third variant of the same
        // reason: it is asked when the application will not start, so it must not need the
        // application to start. See Homespool.Data.SchemaWriter, and note the older-image trap the
        // two applets above share - an image that predates this argument starts the server instead.
        if (args.Length > 0 && args[0] == SchemaWriter.Argument)
        {
            Environment.ExitCode = SchemaWriter.Write(args.Length > 1 ? args[1] : null);
            return;
        }

        // The editable settings this deployment currently carries in its environment, written to the
        // file that now owns them. A one-shot for the upgrade that moved them out of compose.yaml,
        // and the fourth variant of the same not-a-server-run shape - including the older-image trap
        // the three above describe.
        if (args.Length > 0 && args[0] == SettingsWriter.Argument)
        {
            Environment.ExitCode = SettingsWriter.Write(args.Length > 1 ? args[1] : null);
            return;
        }

        Log.Logger = new LoggerConfiguration()
                     .MinimumLevel.Override("Microsoft", LogEventLevel.Information)
                     .Enrich.FromLogContext()
                     .WriteTo.Console(new RenderedCompactJsonFormatter())
                     .CreateBootstrapLogger();

        try
        {
            WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

            // The one writable configuration source, and it is layered LAST - above the environment
            // variables - so a value an administrator saved wins over one an environment variable
            // carries. That ordering is not a preference: compose substitutes its own default into
            // the environment whether or not .env names the variable, so every key compose mentions
            // is always present, and a layer underneath would be silently inert for exactly those
            // keys. The other half of the choice is that a migrated key's compose line is deleted
            // rather than kept as a fallback, so a setting has one home instead of two with an
            // invisible winner.
            //
            // It must be added before anything reads configuration, which starts on the next line.
            SettingsFile settingsFile = new(SettingsFile.Resolve(
                builder.Configuration[SettingsFile.PathConfigurationKey],
                builder.Environment.ContentRootPath));

            builder.Configuration.AddJsonFile(settingsFile.Path, optional: true, reloadOnChange: false);
            builder.Services.AddSingleton(settingsFile);

            // Add services to the container.
            builder.Services.AddSerilog((services, lc) => lc
                                                          .ReadFrom.Configuration(builder.Configuration)
                                                          .ReadFrom.Services(services)
                                                          .Enrich.FromLogContext()
                                                          .WriteTo.Console(new RenderedCompactJsonFormatter()));

            builder.Services.AddHomespoolData(builder.Configuration);

            builder.Services.AddDatabaseDeveloperPageExceptionFilter();

            builder.Services.AddHomespoolDataProtection(builder.Configuration, builder.Environment);

            // This is process-wide and belongs here rather than on a scheme, because what it protects
            // is the DEFAULT for whatever is registered next. AddOidcAuthentication also sets
            // MapInboundClaims = false on its own handler, and that is not redundant with this: this
            // one makes a handler somebody adds later safe without their having to know the rule; that
            // one states it where a reader of the registration can see it. Neither alone is the rule.
            JsonWebTokenHandler.DefaultInboundClaimTypeMap.Clear();

            builder.Services.AddAuthentication()
                            .AddPrusaConnectPrinterAuthentication()
                            .AddApiTokenAuthentication()
                            .AddXApiKeyAuthentication()
                            .AddOidcAuthentication(builder.Configuration);

            builder.Services.AddIdentity<Model.Entities.HSUser, IdentityRole<long>>(Accounts.IdentityConfiguration.Configure)
                            .AddEntityFrameworkStores<HomespoolDbContext>()
                            .AddErrorDescriber<Accounts.HSIdentityErrorDescriber>()
                            .AddDefaultTokenProviders();

            builder.Services.ConfigureApplicationCookie(options =>
            {
                // Cookie settings
                options.Cookie.HttpOnly = true;
                options.ExpireTimeSpan = TimeSpan.FromDays(1);

                // Lax, written down rather than inherited. It is already the framework's default for
                // this cookie, so this changes no behaviour - what it changes is that the value is a
                // decision somebody can find, on the setting that is currently the ONLY thing
                // standing between a cross-site POST and an authenticated /api call: Policies.Api
                // accepts this cookie and no antiforgery guards those actions.
                //
                // NOT Strict, and that was costed rather than assumed. Strict withholds the cookie on
                // every cross-site top-level navigation, not merely on cross-site POSTs - so every
                // link in outgoing mail (confirm, reset, invite) would open the app signed out even in
                // a browser that is signed in, and ConfirmEmailChange, which is designed to be clicked
                // while signed in, would answer NotFound. What it would buy is protection against a
                // cross-site GET with side effects, of which there are none by design: Logout is a
                // POST and the API's GETs are reads. Real friction against a marginal gain.
                options.Cookie.SameSite = SameSiteMode.Lax;

                // SameAsRequest, written down for the same reason as the line above: it is already
                // the framework's default, and it is the sort of value somebody arrives at asking
                // why it is not the stricter one.
                //
                // NOT Always, and the reason is that plaintext deployments are supported rather than
                // tolerated. Always withholds the cookie from every http:// request, so the rig and
                // any deployment run without the proxy could not sign in at all - not degraded,
                // locked out.
                //
                // What SameAsRequest depends on is the application knowing it is behind TLS, which
                // on the shipped stack it does: nginx sends X-Forwarded-Proto and
                // XForwarded__KnownNetworks is defaulted to the proxy's subnet, so Request.IsHttps
                // is true and the cookie is issued Secure. Empty PROXY_NETWORK and the middleware is
                // not registered, the scheme reads http, and this cookie loses Secure - which is a
                // deliberate opt-out that also raises the red insecure-connection banner on every
                // page, rather than a state anybody lands in quietly.
                //
                // Deliberately not read from X-Forwarded-Proto directly: untrusted, that header is
                // attacker-written, so honouring it where no proxy is trusted would be worse than
                // the state it claims to fix.
                options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;

                options.LoginPath = "/Account/Login";
                options.AccessDeniedPath = "/Account/AccessDenied";
                options.SlidingExpiration = true;

                // An unauthenticated /api call answered with a redirect to an HTML login page is
                // useless to a script - and arrives as 200. See ApiStatusCodeCookieEvents.
                ApiStatusCodeCookieEvents.Apply(options);
            });

            // Add services to the container.
            builder.Services.AddAuthorization(Authorisation.Builder.Build);

            // Account/Manage requires a signed-in account. That rule lives on the pages themselves as
            // [Authorize], not here as an AuthorizeFolder convention: a reader auditing one page can
            // see whether it is protected by looking at it, which a path string in Program.cs does not
            // give them. The cost is that a new page under that folder has to say so itself.
            builder.Services.AddRazorPages()
                            .AddDataAnnotationsLocalization(options =>
                                options.DataAnnotationLocalizerProvider = (_, factory) =>
                                    factory.Create(typeof(Localisation.SharedResource)));

            builder.Services.AddHomespoolLocalisation();

            builder.Services.AddControllers(options =>
            {
                options.Conventions.Add(new ApiExplorerVisibilityConvention());

                // A credential scope refusing an action is a 403, not a fault - and mapping it here
                // rather than per action is what keeps a new file endpoint from answering 500.
                options.Filters.Add<Authorisation.CredentialScopeDeniedFilter>();
            });

            builder.Services.AddOpenApi();

            // Validated on start, and every section carrying an editable setting is. A value that
            // used to arrive only from .env now arrives from a file the application writes, so the
            // range that was previously somebody's care while editing compose has to be checked
            // somewhere - and the earliest place that can refuse it is here. The ranges themselves
            // exclude what is broken rather than what is unwise, so this cannot become a new reason a
            // working deployment stops starting.
            builder.Services.AddOptions<PrusaConnect.PrusaConnectOptions>()
                   .Bind(builder.Configuration.GetSection(PrusaConnect.PrusaConnectOptions.SectionName))
                   .ValidateDataAnnotations()
                   .ValidateOnStart();

            builder.Services.AddOptions<Accounts.AttemptLimitOptions>()
                   .Bind(builder.Configuration.GetSection(Accounts.AttemptLimitOptions.SectionName))
                   .ValidateDataAnnotations()
                   .ValidateOnStart();

            builder.Services.Configure<PrusaConnect.PrinterTrafficLogOptions>(
                builder.Configuration.GetSection(PrusaConnect.PrinterTrafficLogOptions.SectionName));

            // Singleton because it owns a file handle, and resolved eagerly below so that turning it
            // on is reported at startup rather than whenever the first printer happens to connect.
            builder.Services.AddSingleton<PrusaConnect.PrinterTrafficLog>();

            // StorageOptions is bound by AddHomespoolData, in a project that does not reference the
            // options DataAnnotations extension. The attributes live on the class there; the
            // validator is added here, where the shared framework already carries it.
            builder.Services.AddOptions<StorageOptions>()
                   .ValidateDataAnnotations()
                   .ValidateOnStart();

            builder.Services.Configure<Certificates.CertificateOptions>(
                builder.Configuration.GetSection(Certificates.CertificateOptions.SectionName));

            // Needed by anything that takes a clock from the container rather than reading
            // TimeProvider.System statically. One is resolvable anyway - something in the
            // Identity/EF/hosting graph provides it - but depending on an incidental registration by
            // another component is a dependency nobody declared, and it would vanish silently if that
            // component changed. This states it.
            builder.Services.AddSingleton(TimeProvider.System);

            // Singleton: it owns files on disk and its whole contract is that the authority is minted
            // once and never again. Nothing about it is per-request.
            builder.Services.AddSingleton<Certificates.PrinterCertificateAuthority>();

            // Singleton alongside the authority it reads: it holds bound options and a path, and
            // reaches the filesystem only when a bundle is actually asked for.
            builder.Services.AddSingleton<PrusaConnect.ProvisioningBundleBuilder>();

            // Answers "could a printer reach this name?" by resolving it, rather than by guessing from
            // how the name looks - which is the only way to tell a container's hostname from a real one.
            builder.Services.AddSingleton<Certificates.IHostAddressResolver, Certificates.DnsHostAddressResolver>();

            ConfigureListeners(builder);

            AddForwardedHeaders(builder);

            builder.Services.AddOptions<Mail.SmtpOptions>()
                   .Bind(builder.Configuration.GetSection(Mail.SmtpOptions.SectionName))
                   .ValidateDataAnnotations()
                   .ValidateOnStart();

            // The settings file holds one credential, and it is stored encrypted. The post-configure
            // decrypts it into SmtpOptions.Password after binding, so every consumer keeps reading
            // the plain property and none of them knows protection exists.
            builder.Services.AddSingleton<SettingsSecretProtector>();
            builder.Services.AddScoped<SettingsStore>();
            builder.Services
                   .AddSingleton<IPostConfigureOptions<Mail.SmtpOptions>, Mail.SmtpPasswordUnprotector>();

            builder.Services.AddOptions<Accounts.InvitationOptions>()
                   .Bind(builder.Configuration.GetSection(Accounts.InvitationOptions.SectionName))
                   .ValidateDataAnnotations()
                   .ValidateOnStart();

            builder.Services.Configure<Middleware.SecurityOptions>(
                builder.Configuration.GetSection(Middleware.SecurityOptions.SectionName));

            Mail.SmtpOptions smtpOptions = new();
            builder.Configuration.GetSection(Mail.SmtpOptions.SectionName).Bind(smtpOptions);

            // Stateless, so a singleton is fine; it exists purely so tests can substitute a fake transport.
            builder.Services.AddSingleton<Mail.ISmtpTransportFactory, Mail.MailKitSmtpTransportFactory>();
            builder.Services.AddSingleton<Mail.SmtpConnectivityCheck>();

            // Which sender is registered is decided by configuration alone, never by probing the network, so that a
            // mail server being down cannot quietly change how accounts are created. See SmtpOptions.IsConfigured.
            if (smtpOptions.IsConfigured)
            {
                builder.Services.AddScoped<Mail.IEmailSender, Mail.SmtpEmailSender>();

                // Only with a mail server to send through - otherwise this is a background service
                // whose whole job is to log that it cannot do its job. The banner and /health cover
                // deployments without SMTP.
                builder.Services.AddHostedService<Health.TelemetryAlertService>();
            }
            else
            {
                builder.Services.AddScoped<Mail.IEmailSender, Mail.LoggingEmailSender>();
            }

            builder.Services.AddHostedService<Mail.SmtpConnectivityProbe>();

            // Resolves the "confirm accounts at creation" rule once from SmtpOptions, so account-creation
            // pages inject this instead of SmtpOptions. Singleton: SMTP config is fixed at startup.
            builder.Services.AddSingleton<Mail.AccountConfirmationPolicy>();

            // Holds the first-run bootstrap secret and the one-way "an admin exists" flag; seeded once
            // by SeedAdminBootstrap after migration. Singleton so the flag is process-wide.
            builder.Services.AddSingleton<Accounts.SetupState>();

            // Factory-activated (IMiddleware) so it is resolved from the container. Singleton: it holds
            // no per-request state, only the singleton SetupState.
            builder.Services.AddSingleton<Middleware.SetupGateMiddleware>();

            // Likewise factory-activated, and it holds nothing at all.
            builder.Services.AddSingleton<Middleware.SecurityHeadersMiddleware>();
            builder.Services.AddSingleton<Middleware.ClientGoneMiddleware>();

            builder.Services.AddScoped<PrusaConnect.PrusaConnectService>()
                            .AddScoped<PrusaConnect.WebSocketHandler>()
                            .AddScoped<PrusaConnect.TokenService>()
                            .AddScoped<PrusaConnect.CodeGenerator>()
                            .AddScoped<Accounts.AttemptLimiter>()
                            .AddScoped<PrusaConnect.MessageDispatcher>()
                            .AddScoped<Printing.PrinterCommandService>()
                            .AddScoped<Printing.ToolTargetReader>()
                            .AddScoped<PrusaConnect.PrinterPreheatService>()
                            .AddScoped<PrusaConnect.PrinterFilamentService>();

            // Plain singletons, not TelemetryWriter's singleton-with-IServiceScopeFactory pattern below:
            // neither touches HomespoolDbContext, only in-memory state (the directory of live connection
            // actors, and the actors' own singleton dependencies), so there is no scoped dependency
            // to protect against capturing. The actors themselves are not registered at all - one is
            // created per accepted WebSocket by the factory and lives exactly as long as that request.
            builder.Services.AddSingleton<Printing.PrinterConnectionRegistry>();
            builder.Services.AddSingleton<PrusaConnect.PrinterConnectionActorFactory>();

            // The exception to "not registered at all": a printer on the pre-websocket HTTP transport
            // has no request to own its actor, so this singleton does - one per printer, created on
            // its first POST and reaped when it goes quiet. Registered once as itself, so the
            // controller and the hosted-service host share the instance holding the sessions.
            builder.Services.AddSingleton<PrusaConnect.HttpPrinterSessions>();
            builder.Services.AddHostedService(sp => sp.GetRequiredService<PrusaConnect.HttpPrinterSessions>());

            // Singleton because its whole value is accumulating across connections and printers:
            // "this firmware sends a field we do not model" is a fact about the deployment, and a
            // per-request instance would forget it between messages.
            builder.Services.AddSingleton<PrusaConnect.UnknownFieldTracker>();

            // One store, two faces: actors resolve hashes through ITransferContentStore, request
            // handlers register files through ITransferOffers. Singleton because an offer has to
            // outlive the request that made it - the printer collects it on its own schedule.
            builder.Services.AddSingleton<PrusaConnect.Transfers.TransferOfferStore>();
            builder.Services.AddSingleton<PrusaConnect.Transfers.ITransferContentStore>(sp => sp
                .GetRequiredService<PrusaConnect.Transfers.TransferOfferStore>());
            builder.Services.AddSingleton<PrusaConnect.Transfers.ITransferOffers>(sp => sp
                                                                                      .GetRequiredService<
                                                                                          PrusaConnect.Transfers.
                                                                                          TransferOfferStore>());

            // The keys behind encrypted downloads, beside the offers rather than inside them: the
            // store pins bytes and knows nothing of ciphers, which the inline path relies on.
            builder.Services.AddSingleton<PrusaConnect.Transfers.EncryptedTransferOffers>();

            // Uploaded gcode: options, the store, and the content-root accessor it needs. Singleton
            // because the store holds no per-request state - it is a path and a couple of rules.
            builder.Services.AddOptions<PrintFiles.PrintFileStorageOptions>()
                   .Bind(builder.Configuration.GetSection(PrintFiles.PrintFileStorageOptions.SectionName))
                   .ValidateDataAnnotations()
                   .ValidateOnStart();
            builder.Services.AddSingleton<IHostEnvironmentAccessor>(sp => new HostEnvironmentAccessor(
                                                                        sp.GetRequiredService<IWebHostEnvironment>()
                                                                          .ContentRootPath));
            builder.Services.AddSingleton<PrintFiles.UserFileStore>();

            // Scoped, because it holds a DbContext - which is exactly why the index lives here rather
            // than inside the singleton store. Everything that changes a file goes through this so the
            // disk and the table cannot drift; reads pass straight through.
            builder.Services.AddScoped<PrintFiles.PrintFileCatalog>();

            // Runs once at startup, after MigrateHomespoolData below has made the tables exist.
            builder.Services.AddHostedService<PrintFiles.PrintFileReconciler>();

            // Cameras: options, the guarded HTTP client, the fetcher and the frame cache. The
            // handler carries the address policy, which reads as networking plumbing here and lives
            // in Cameras/Registration.cs instead.
            builder.Services.AddCameras(builder.Configuration);

            // Scoped, following the command service it wraps. Shared by the API endpoint and the
            // Files page so that "a send that did not take leaves no offer" has one implementation.
            builder.Services.AddScoped<Printing.PrintFileSender>();

            AddPrinterEndpointRateLimiting(builder);

            // Scoped, following the WebSocketHandler it runs: one session per accepted upgrade.
            builder.Services.AddScoped<PrusaConnect.PrinterConnectionSession>();

            // Singleton, not scoped like its neighbors above: one drain loop and one in-memory
            // live-state cache for the whole process, fed by every request's scoped
            // MessageDispatcher through the ITelemetrySink interface - so a request never hands
            // the writer its own HomespoolDbContext, only a DTO.
            //
            // The writer still needs HomespoolDbContext to persist, which is the usual trap for a
            // singleton: inject the scoped context directly and it gets captured once, reused
            // forever, single-threaded and stale, for the life of the process. TelemetryWriter
            // avoids this by injecting IServiceScopeFactory instead - itself a singleton, safe to
            // hold - and calling CreateScope() fresh in HydrateAsync and FlushAsync, each wrapped
            // in a `using` that disposes the scope (and its HomespoolDbContext) the moment that one
            // read or write finishes. No HomespoolDbContext field ever exists on TelemetryWriter itself.
            builder.Services.AddSingleton<Telemetry.TelemetryWriter>();
            builder.Services.AddSingleton<Telemetry.ITelemetrySink>(sp => sp.GetRequiredService<Telemetry.TelemetryWriter>());
            builder.Services.AddSingleton<Telemetry.ITelemetryHealthSource>(sp => sp.GetRequiredService<Telemetry.TelemetryWriter>());
            builder.Services.AddSingleton<Telemetry.ITelemetryEviction>(sp => sp.GetRequiredService<Telemetry.TelemetryWriter>());
            builder.Services.AddHostedService(sp => sp.GetRequiredService<Telemetry.TelemetryWriter>());

            // The middle link in a three-part budget that no single file used to own: the writer's
            // shutdown flush must finish inside this, and this must finish inside the container
            // runtime's stop grace period (compose.yaml, stop_grace_period). Derived rather than
            // written as a number, so raising FinalFlushAttempts cannot silently outgrow it - which
            // is exactly what happened with the framework's 30 s default, where three attempts that
            // could each block ~10 s landed on the timeout and every shutdown against a stuck
            // database was killed mid-drain, losing the buffers and the log line naming the loss.
            // Two terms, not one: the drain cannot start until the flush already in flight when
            // SIGTERM arrived has finished, and that one runs to the ordinary busy budget. Omitting
            // it put the timeout below the drain's real worst case, so the process was still killed
            // mid-shutdown - just 19 s sooner than before.
            StorageOptions shutdownStorageOptions = builder.Configuration
                                                           .GetSection(StorageOptions.SectionName)
                                                           .Get<StorageOptions>() ?? new StorageOptions();

            builder.Services.Configure<HostOptions>(options =>
                                                        options.ShutdownTimeout =
                                                            Telemetry.TelemetryWriter.MaxShutdownFlushDuration +
                                                            TimeSpan.FromMilliseconds(shutdownStorageOptions.BusyTimeoutMilliseconds) +
                                                            TimeSpan.FromSeconds(1.5));

            // The process answering requests says nothing about whether it is still recording
            // anything - a flush bug once made every write fail permanently while the service looked
            // entirely healthy from outside. This is the hook a monitoring system can watch.
            // Tagged, because the two endpoints below must not report the same thing. Only checks
            // tagged "live" answer /health/live, and only a fault a restart would fix may carry that
            // tag - see TelemetryWriterLivenessHealthCheck.
            builder.Services.AddHealthChecks()
                   .AddCheck<Telemetry.TelemetryPersistenceHealthCheck>("telemetry-persistence")
                   .AddCheck<Telemetry.TelemetryWriterLivenessHealthCheck>("telemetry-writer-alive", tags: [LivenessTag])

                   // Deliberately untagged: a certificate that no longer matches this machine is not a
                   // fault a restart fixes, and the banner picks it up from the report either way.
                   .AddCheck<Certificates.PrinterCertificateHealthCheck>("printer-certificate")

                   // Also untagged: a deployment handing tokens to the internet is misconfigured, not
                   // broken, and a restart would faithfully reproduce it.
                   .AddCheck<Health.DeploymentExposureHealthCheck>("deployment-exposure")

                   // Untagged for the same reason. Cameras stop working entirely without a sidecar
                   // credential, and the person who can fix that otherwise sees only blank cameras.
                   .AddCheck<Cameras.CameraCredentialHealthCheck>("camera-credential")

                   // Untagged again, and the quietest failure of the three: with no address to send
                   // video to, the live-view button simply never appears, which is indistinguishable
                   // from a feature that was never built.
                   .AddCheck<Cameras.WebRtcCandidateHealthCheck>("camera-live-view");

            // Sweeps TelemetrySample rows past StorageOptions.TelemetryRetentionDays. No interface
            // registration needed, unlike TelemetryWriter above - nothing else ever needs to reach it.
            builder.Services.AddHostedService<Telemetry.TelemetryRetentionService>();

            // Sweeps PrusaConnectRegistration rows whose code has expired. Nothing else ever removed
            // one, and POST /p/register is anonymous - so without this the only bound on the table is
            // the rate limiter, which counts requests rather than rows.
            builder.Services.AddHostedService<PrusaConnect.RegistrationRetentionService>();

            // Scoped so its per-request memo of "may this account touch this printer" is bounded by
            // the request, which is the only window in which the answer cannot change.
            builder.Services.AddScoped<Authorisation.TeamCapabilityLookup>();
            builder.Services.AddScoped<Authorisation.PrinterAccessService>();

            // Scoped, unlike their singleton neighbors above, because they hold the scoped HomespoolDbContext.
            builder.Services.AddScoped<Accounts.TeamService>();
            builder.Services.AddScoped<Services.UnitOfWork>();
            builder.Services.AddScoped<Accounts.InvitationService>();
            builder.Services.AddScoped<Services.PrinterQueryService>();
            builder.Services.AddScoped<Services.PrinterRemovalService>();
            builder.Services.AddScoped<Accounts.UserNameLookup>();
            builder.Services.AddScoped<PrintQueueService>();
            builder.Services.AddScoped<Printing.PrintHistoryService>();
            builder.Services.AddScoped<Printing.PrintStopService>();
            builder.Services.AddScoped<Queue.QueueSnapshotReader>();

            // The producer loop and the poke that saves it waiting out a tick. Singletons: the signal
            // is process-wide by nature, and the advancer opens its own scope per pass because a
            // DbContext must not outlive one.
            builder.Services.AddSingleton<QueueSignal>();

            // Resolvable as itself as well as a hosted service, following TelemetryWriter: a test
            // needs to drive one pass deterministically rather than wait out a poll interval.
            builder.Services.AddSingleton<QueueAdvancer>();
            builder.Services.AddHostedService(sp => sp.GetRequiredService<QueueAdvancer>());
            builder.Services.AddScoped<Accounts.ApiTokenService>();

            WebApplication app = builder.Build();

            // Ctrl-C/SIGTERM is otherwise silent: the framework's own "Application is shutting
            // down..." comes from Microsoft.Hosting.Lifetime, and Serilog's Microsoft override
            // (appsettings.json) filters that namespace to Warning. An operator watching a blank
            // console while telemetry drains has no way to tell progress from a hang, and reaches
            // for SIGKILL - which is exactly what loses the buffered samples. TelemetryWriter logs
            // the matching "drained" or "unwritten" line when it finishes.
            app.Lifetime.ApplicationStopping.Register(() =>
                                                          app.Logger.LogInformation(
                                                              "Shutting down: draining buffered telemetry to the database. Please let this finish."));

            // Apply migrations on service startup. (assuming StorageOptions have enabled it).
            app.Services.MigrateHomespoolData();

            // Ensure the admin role exists and, if no administrator has been created yet, mint and log
            // the one-time /setup token. Runs inline so setup state is settled before the first request.
            Accounts.AdminBootstrap.SeedAdminBootstrap(app.Services);

            // The certificate nginx presents to printers. Inline, before Run, because the proxy waits
            // on this container's health check and then reads the leaf off the shared volume.
            EnsurePrinterCertificate(app);

            // Resolved here only so that its "this is ON" warning is emitted at startup. Left to the
            // container it would be constructed when the first printer connects, which is both later
            // and buried - and something writing message bodies to disk should say so where an
            // operator scanning a boot log will see it.
            app.Services.GetRequiredService<PrusaConnect.PrinterTrafficLog>();

            // Configure the HTTP request pipeline.
            if (app.Environment.IsDevelopment())
            {
                app.MapOpenApi().SegregateByListener();
                app.MapScalarApiReference().SegregateByListener();
            }

            // FIRST, and both halves of that matter.
            //
            // Before UseHttpsRedirection: that middleware reads Request.Scheme, so behind a
            // TLS-terminating proxy it would see "http", answer 307 to https, and the proxy would
            // forward the retry as http again - a redirect loop rather than a subtle bug.
            //
            // Before UseSerilogRequestLogging: otherwise every logged request carries the proxy's
            // address instead of the client's, which is the thing this exists to fix.
            //
            // Applied to the printer listener too, which reverses what decision 3a said, because the
            // fact it rested on has changed: printers used to reach Kestrel directly, so a forwarded
            // header on that listener was attacker-supplied by definition. nginx now terminates
            // printer TLS as well, the port is not published, and the proxy is the only thing that can
            // reach it - so X-Real-IP there is the proxy's word, exactly as it is for users. Without
            // this a printer's real address disappears from the logs and becomes the proxy's, which is
            // the diagnostic that finds a misbehaving printer on a LAN.
            //
            // Keyed on the port the connection arrived on rather than on the path, for the reason
            // ListenerSegregationMiddleware gives at greater length: the port is a property of the
            // socket and no header changes it. It also has to be the port here, because this runs
            // before routing and there is no endpoint to ask yet.
            //
            // The exception is PrinterTls=false, where printers connect to that port directly again and the
            // header goes back to being written by whoever connected. One setting, both ends.
            //
            // Registered ONLY when something is actually trusted. Clearing the framework's known
            // networks and adding nothing does not mean "trust nobody" - ASP.NET skips the peer check
            // entirely when both lists are empty, which means "trust anybody". Proven by probe:
            // unconfigured, a loopback client's X-Forwarded-Proto was honoured; trusting 10.0.0.0/8
            // instead, the same request was ignored. Leaving the middleware out is unambiguous.
            if (app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<Middleware.XForwardedOptions>>().Value
                   .TrustsAnything)
            {
                int printerPort = ReadListenerOptions(builder.Configuration).PrinterPort;
                bool printerListenerIsProxied = PrinterTransportIsSecure(app.Services);

                app.UseWhen(
                    Listeners.ForwardedHeaderScope.Predicate(printerPort, printerListenerIsProxied),
                    branch => branch.UseForwardedHeaders());
            }

            // As early as anything that answers, which is the point: the headers are set on the way in,
            // so a response short-circuited further down - an HTTPS redirect, a segregation 404, a
            // rate-limiter 429 - carries them as well. It goes after the forwarded-headers block only
            // because that block reads the request and writes no response.
            app.UseMiddleware<Middleware.SecurityHeadersMiddleware>();

            // Log HTTP requests with Serilog, order of this matters.
            // Requests handled before in the pipeline are NOT logged.
            app.UseSerilogRequestLogging();

            // INSIDE the request logging, deliberately. It absorbs the cancellation and sets 499, and
            // Serilog reads the status on the way back out - registered outside it instead, Serilog
            // would already have logged the 500 and the unhandled exception it exists to prevent.
            app.UseMiddleware<Middleware.ClientGoneMiddleware>();

            // Only when this process serves users over TLS itself. Otherwise there is no port to
            // redirect to that is not the printer's, and sending a browser there is worse than not
            // redirecting at all - see the pinned HttpsPort in ConfigureListeners.
            //
            // Everything except /health. A probe runs inside the container over plain HTTP, and a
            // 307 to https is not a failure to curl - so with redirection applied, a monitoring
            // check would report success without ever reaching the health endpoint. Excluding the
            // path keeps the probe honest wherever TLS is terminated.
            if (ReadListenerOptions(builder.Configuration).UserHttpsPort is not null)
            {
                app.UseWhen(
                    context => !context.Request.Path.StartsWithSegments(HealthEndpointPath, StringComparison.OrdinalIgnoreCase),
                    branch => branch.UseHttpsRedirection());
            }

            app.UseRouting();

            // Immediately after routing, because it needs the matched endpoint and nothing else
            // should happen first: an endpoint reached on the wrong listener is refused before it can
            // cost a rate-limiter permit, an authentication round trip or any database work. Ahead of
            // the setup gate too, so a probe on the wrong listener gets the same 404 before the first
            // administrator exists as after.
            app.UseMiddleware<Listeners.ListenerSegregationMiddleware>();

            // Before an administrator exists, funnel every navigable page to /setup. No-op once setup
            // completes. Placed after routing so static-asset and printer endpoints resolve normally.
            app.UseMiddleware<Middleware.SetupGateMiddleware>();

            // After UseRouting, so the endpoint's [EnableRateLimiting] metadata is resolved, and
            // before authentication, so a rejected request costs no database work.
            app.UseRateLimiter();

            app.UseAuthentication();
            app.UseAuthorization();

            // After authorization, so it sees a resolved principal and cannot be reached by anybody
            // an endpoint would have refused anyway. It is inert unless Security:RequireTwoFactor is
            // on, and it only ever acts on the application cookie - see the middleware's remarks.
            app.UseMiddleware<Middleware.TwoFactorEnrolmentMiddleware>();

            // After authentication, and that ordering is load-bearing rather than tidy: the first
            // culture provider reads the signed-in account's stored language, so it needs
            // HttpContext.User populated. Placed before it - which is where most guidance puts
            // request localisation - the provider sees an anonymous request every time and silently
            // never fires, leaving Accept-Language to decide for somebody who had chosen.
            app.UseRequestLocalization(
                app.Services.GetRequiredService<IOptions<RequestLocalizationOptions>>().Value);

            // Anonymous by design: a monitoring system holds no credentials, and the response carries
            // only counters and timestamps about this service's own write path - nothing about
            // printers, jobs or users.
            //
            // Everything, for monitoring and for humans. Alert on this; never restart on it.
            app.MapHealthChecks(HealthEndpointPath, new HealthCheckOptions
            {
                ResponseWriter = WriteHealthResponseAsync,
            }).SegregateByListener();

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
            }).SegregateByListener();

            // Every Map... call is segregated, including the ones that look like they could not
            // possibly need it. An endpoint that reaches the pipeline unclassified is refused on every
            // listener rather than served on the wrong one, so forgetting this fails loudly here
            // instead of quietly widening a boundary - see ListenerSegregation.
            app.MapControllers().SegregateByListener();

            app.MapStaticAssets().SegregateByListener();
            app.MapRazorPages()
               .WithStaticAssets()
               .SegregateByListener();

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
    /// Rate-limit policy for the pre-websocket HTTP transport - <c>POST /p/telemetry</c> and
    /// <c>POST /p/events</c>.
    /// </summary>
    /// <remarks>
    /// <b>Its own policy, because the traffic shape is the opposite of the socket's.</b> An upgrade
    /// happens once per connection, so <see cref="PrinterSocketRateLimitPolicy"/>'s 120/minute covers
    /// a whole fleet; this transport posts roughly once a second <em>per printer</em>, so sharing that
    /// window would let two printers exhaust it and throttle every printer as a matter of course.
    /// </remarks>
    internal const string PrinterHttpTransportRateLimitPolicy = "printer-http-transport";

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
    /// service is behind a reverse proxy (<c>PRINTER_HOST</c>/<c>PRINTER_TLS</c>), and nothing here
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
    /// attempt cap is - but it turns "unlimited" into "bounded".
    /// </para>
    /// <para>
    /// The login form is <em>not</em> rate-limited here, and deliberately so: Identity's account
    /// lockout now bounds password guessing per account (see <c>Login.cshtml.cs</c>), which is both
    /// proxy-agnostic and impossible to evade by rotating source addresses. A global limiter on login
    /// would instead let one attacker lock out every legitimate user at once.
    /// </para>
    /// </remarks>
    /// <summary>
    /// Translates <see cref="Middleware.XForwardedOptions"/> onto the framework's forwarded-headers
    /// middleware, and says at startup what it ended up trusting.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The framework middleware does the security-relevant part - checking the immediate peer against
    /// the known proxies before believing anything - so this only supplies it with what to trust and
    /// which header to read. Hand-rolling the header parsing was considered and rejected: the entry
    /// selection is exactly where this class of bug lives.
    /// </para>
    /// <para>
    /// <b>An unconfigured deployment is safe but inert</b>, because the framework then trusts loopback
    /// alone and a container proxy is not on loopback. That failure is silent - mail keeps saying
    /// <c>http://</c> - so it is logged rather than left to be discovered. This repository has
    /// declared a rule and never run it four times over; this is the same shape, caught at startup.
    /// </para>
    /// </remarks>
    private static void AddForwardedHeaders(WebApplicationBuilder builder)
    {
        Middleware.XForwardedOptions forwarded = new();
        builder.Configuration.GetSection(Middleware.XForwardedOptions.SectionName).Bind(forwarded);

        builder.Services.Configure<Middleware.XForwardedOptions>(
            builder.Configuration.GetSection(Middleware.XForwardedOptions.SectionName));

        builder.Services.Configure<ForwardedHeadersOptions>(options =>
                                                                Middleware.ForwardedHeadersConfigurator.Apply(
                                                                    forwarded, options, Log.Warning));

        if (forwarded.TrustsAnything)
        {
            Log.Information("Trusting {Header} from {ProxyCount} proxy address(es) and {NetworkCount} network(s).",
                            forwarded.ClientAddressHeader, forwarded.KnownProxies.Length, forwarded.KnownNetworks.Length);
        }
        else
        {
            Log.Warning("No proxy is trusted (XForwarded:KnownProxies and :KnownNetworks are both empty), so "
                        + "forwarded headers are ignored except from loopback. If this deployment sits behind a "
                        + "reverse proxy, links in outgoing mail will say http:// and client addresses in the log "
                        + "will be the proxy's. Set XForwarded:KnownNetworks to the proxy's network.");
        }
    }

    /// <summary>
    /// Binds the listeners — plain HTTP for people, plain HTTP for printers — and the classification
    /// middleware that keeps each set of routes on its own.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Configuring any endpoint here means configuring all of them.</b> Kestrel ignores
    /// <c>ASPNETCORE_URLS</c> / <c>applicationUrl</c> entirely once endpoints are set in code — it
    /// logs "Overriding address(es)" and binds these instead — so the user listener is named here too
    /// rather than left to the environment. <c>Listeners:UserPort</c> defaults to the 8080 the base
    /// image already used, so a deployment that sets nothing keeps the port it had.
    /// </para>
    /// <para>
    /// <b>Kestrel no longer terminates TLS for printers, and that reverses a recorded decision.</b>
    /// It used to mint the leaf here and serve it on this very listener; it does neither, because
    /// <see cref="System.Net.Security.SslStream"/> ignores the RFC 6066 <c>max_fragment_length</c> a
    /// printer negotiates and OpenSSL honours it. A printer holds 1024 bytes of TLS plaintext at a
    /// time, so a record larger than that kills every file transfer — which is what shipped, until
    /// nginx was moved in front of this listener too. The leaf is still ours:
    /// <see cref="EnsurePrinterCertificate"/> mints it
    /// on the startup path, and nginx presents it.
    /// </para>
    /// <para>
    /// <b>The split outlived the certificate that motivated it, deliberately.</b> With TLS gone from
    /// both listeners they are two plain HTTP ports, and one would do — except that the boundary is
    /// the point. <c>/p/*</c> exists on the printer listener alone, enforced by
    /// <see cref="ConnectionInfo.LocalPort"/>; collapsing them would leave a line of nginx
    /// configuration as the only thing between a proxied user request and the printer protocol,
    /// turning a structural guarantee into a configuration one. Two ports cost four lines.
    /// </para>
    /// </remarks>
    private static void ConfigureListeners(WebApplicationBuilder builder)
    {
        builder.Services.Configure<Listeners.ListenerOptions>(
            builder.Configuration.GetSection(Listeners.ListenerOptions.SectionName));

        // Factory-activated (IMiddleware) like the setup gate, so it is resolved from the container.
        // Singleton: it holds the bound options and nothing per-request.
        builder.Services.AddSingleton<Listeners.ListenerSegregationMiddleware>();

        // Pinned rather than left to be discovered. It guarded against a measured failure: the printer
        // listener used to be this process's only HTTPS endpoint, so the redirection middleware found
        // it and answered plain-HTTP user requests with a 307 to the *printer* port - GET / on the
        // user listener returned 307 to https://...:15443/ the first time both listeners came up.
        // That endpoint is gone and discovery would now find the right port on its own, so this is no
        // longer load-bearing; it is kept because it costs a line and because the next person to add
        // an HTTPS listener here should find the trap written down rather than measure it again.
        // Null when no user-facing HTTPS port exists, which is also why the middleware itself is only
        // registered in that case: when a proxy terminates TLS, redirecting to https is the proxy's
        // job and it knows the public port - this process does not.
        builder.Services.AddHttpsRedirection(options =>
                                                 options.HttpsPort = ReadListenerOptions(builder.Configuration).UserHttpsPort);

        builder.WebHost.ConfigureKestrel(options =>
        {
            Listeners.ListenerOptions listeners = options.ApplicationServices
                                                         .GetRequiredService<Microsoft.Extensions.Options.IOptions<
                                                             Listeners.ListenerOptions>>().Value;

            listeners.Validate();

            options.ListenAnyIP(listeners.UserPort);

            if (listeners.UserHttpsPort is int userHttpsPort)
            {
                options.ListenAnyIP(userHttpsPort, listen => listen.UseHttps());
            }

            // Plain HTTP, and the same line whichever way the deployment is configured. With
            // PrusaConnect:PrinterTls on, nginx terminates in front of this port and it is never
            // published; with it off, this port is published directly and the wire is readable. The
            // difference is what sits in front, which is compose.yaml's business rather than this
            // process's - so there is one listener here and no branch.
            options.ListenAnyIP(listeners.PrinterPort);

            // Plain HTTP and never anything else - see ListenerOptions.TransferPort. The one listener
            // whose being unencrypted is the design rather than a proxy's business.
            options.ListenAnyIP(listeners.TransferPort);
        });
    }

    /// <summary>
    /// Mints the authority and the leaf nginx presents to printers, unless this deployment has turned
    /// printer TLS off.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Before the first request rather than on demand</b>, and now for nginx's sake rather than
    /// Kestrel's: the proxy reads <c>printer-leaf.pem</c> off the shared volume when it starts, and
    /// <c>compose.yaml</c> holds it back until this container reports healthy, which is after this
    /// runs. A leaf minted lazily would be minted after the thing that needs it had already given up.
    /// </para>
    /// <para>
    /// It writes PEM as well as PKCS#12 (<see cref="Certificates.PrinterCertificateAuthority"/>), with
    /// the leaf <i>alone</i> in the certificate file — firmware's
    /// <c>x509_crt_check_ee_locally_trusted</c> requires exactly one certificate presented, and a
    /// terminator that appends the authority fails in a way that reads as a protocol bug.
    /// </para>
    /// </remarks>
    private static void EnsurePrinterCertificate(WebApplication app)
    {
        if (!PrinterTransportIsSecure(app.Services))
        {
            // Plaintext, so the wire can be read. Nothing else in this project can produce a legible
            // capture of the printer protocol: TLS is the point of the path, and a capture of it is
            // ciphertext.
            Log.Warning("Printers reach this deployment in PLAINTEXT because PrusaConnect:PrinterTls is false, and no " +
                        "certificate is issued while it is off. Every printer token crosses the network in clear, in " +
                        "both directions - the one on the USB stick and the one issued at claim. This is for a capture " +
                        "or a rig on a network you control; it is not a deployment setting. The proxy has no printer " +
                        "certificate to serve either, so publish Listeners:PrinterPort directly.");

            return;
        }

        Certificates.PrinterCertificateAuthority authority =
            app.Services.GetRequiredService<Certificates.PrinterCertificateAuthority>();

        // The authority explicitly, not only via the leaf: with an existing leaf nothing below opens
        // the authority's key, so a wrong or missing passphrase would otherwise surface days later,
        // when somebody builds a provisioning bundle - instead of here, at the boot right after the
        // configuration changed.
        authority.EnsureAuthority().Dispose();
        authority.EnsureLeaf(PrinterCertificateNames(app.Services)).Dispose();
    }

    /// <summary>
    /// Whether printers reach this deployment over TLS — which is one question, not two.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b><c>PrusaConnect:PrinterTls</c> decides whether a leaf is minted as well as what the ini
    /// says</b>, and that is deliberate rather than convenient. It no longer decides the listener,
    /// because the listener is plain HTTP either way — what changes is whether nginx stands in front
    /// of it holding the leaf. The two ends still cannot disagree: with it off nothing is issued, so
    /// the proxy has nothing to present even if an operator wired it up anyway.
    /// </para>
    /// <para>
    /// Two settings for one fact is the failure this project keeps finding: they disagree, every
    /// printer fails to connect, and neither value is wrong on its own so nothing can report it. One
    /// setting cannot disagree with itself.
    /// </para>
    /// <para>
    /// It also decides whether forwarded headers are honoured on the printer listener — see
    /// <see cref="AddForwardedHeaders"/>. With nginx in front, <c>X-Real-IP</c> on that listener comes
    /// from the proxy and nothing else can reach the port; without it, a printer connects directly and
    /// the same header is written by whoever connected.
    /// </para>
    /// </remarks>
    private static bool PrinterTransportIsSecure(IServiceProvider services)
    {
        return services.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<PrusaConnect.PrusaConnectOptions>>()
                       .CurrentValue.PrinterTls;
    }

    /// <summary>
    /// Binds <see cref="Listeners.ListenerOptions"/> straight from configuration, for the two places
    /// that need it before the container exists.
    /// </summary>
    private static Listeners.ListenerOptions ReadListenerOptions(IConfiguration configuration)
    {
        Listeners.ListenerOptions listeners = new();
        configuration.GetSection(Listeners.ListenerOptions.SectionName).Bind(listeners);

        return listeners;
    }

    /// <summary>
    /// Every name a printer might be told to reach this server by, for the first run's leaf.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Everything plausible, rather than one address chosen correctly.</b> The leaf covers what
    /// <c>PrusaConnect:PrinterHost</c> says <i>and</i> every address this machine can see, so the
    /// operator picking the wrong one costs a re-downloaded provisioning bundle instead of a
    /// re-provisioned printer. That is the same multi-name hedge that makes a moved DHCP lease
    /// survivable, doing a second job - and it is why nothing asks the operator to name this machine
    /// at first run - nobody stores the answer.
    /// </para>
    /// <para>
    /// The configured host goes first because <see cref="Certificates.PrinterCertificateAuthority"/>
    /// takes the first name as the subject: the one an operator deliberately set is the one worth
    /// seeing when a human inspects the certificate.
    /// </para>
    /// </remarks>
    [SuppressMessage("Usage", "VSTHRD002:Avoid problematic synchronous waits",
                     Justification =
                         "Runs during startup, before the server accepts connections, so there is nothing to deadlock against and no asynchronous caller to yield to.")]
    private static IReadOnlyList<string> PrinterCertificateNames(IServiceProvider services)
    {
        PrusaConnect.PrusaConnectOptions connect = services
                                                   .GetRequiredService<Microsoft.Extensions.Options.IOptions<
                                                       PrusaConnect.PrusaConnectOptions>>().Value;

        Certificates.CertificateOptions certificates = services
                                                       .GetRequiredService<Microsoft.Extensions.Options.IOptions<
                                                           Certificates.CertificateOptions>>().Value;

        // Blocking here is deliberate and bounded: this runs on the startup path, before the first
        // request and before the proxy is let through to read the leaf, so there is nothing to be
        // asynchronous for yet. The resolver caps each lookup, and only detected names are asked -
        // the configured host is taken as given.
        List<string> names =
        [
            .. Certificates.PrinterCertificateNames.ForThisMachineAsync(
                connect,
                certificates.ParsedContainerNetworks,
                services.GetRequiredService<Certificates.IHostAddressResolver>(),
                System.Threading.CancellationToken.None).GetAwaiter().GetResult()
        ];

        if (names.Count == 0)
        {
            // A machine with no usable address and no configured host: the proxy still has to have
            // something to present, but nothing will be able to verify it, so say why now rather than
            // leaving an unexplained TLS failure at the printer.
            Log.Warning("No printer-facing address could be detected and PrusaConnect:PrinterHost is not set, so the "
                        + "printer certificate covers only localhost. Set PrusaConnect:PrinterHost and delete the "
                        + "generated printer-leaf.pem and printer-leaf.key.pem to have one issued that printers can "
                        + "actually verify.");

            names.Add("localhost");
        }

        return names;
    }

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
                // A printer holding a stale token retries roughly once a minute (observed), so
                // this is ample for a fleet while still
                // bounding an attacker probing tokens.
                limiter.PermitLimit = 120;
                limiter.Window = TimeSpan.FromMinutes(1);
                limiter.QueueLimit = 0;
            });

            options.AddFixedWindowLimiter(PrinterHttpTransportRateLimitPolicy, limiter =>
            {
                // Sized for the wire rather than for a connection attempt: firmware's HTTP transport
                // posts telemetry every 1-4s per printer and events on top, so one printer alone can
                // spend ~90/minute. This covers a ten-printer fleet with headroom.
                //
                // Global rather than per printer, and that is a limitation rather than a choice: this
                // middleware runs before UseAuthentication (deliberately, so a rejected request costs
                // no database work), so no identity is resolved when the partition key is computed.
                // The Fingerprint header is the only pre-auth identity available and an attacker can
                // mint a fresh one per request, which buys isolation between honest printers at the
                // cost of an unbounded aggregate - the opposite of what the threat model asks
                // for. One window for the transport keeps that ceiling.
                limiter.PermitLimit = 1200;
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
        context.Response.ContentType = MediaTypeNames.Application.Json;

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
