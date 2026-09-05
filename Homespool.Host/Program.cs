using System;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;

using Serilog;
using Serilog.Events;
using Serilog.Formatting.Compact;

using Homespool.Data;
using Homespool.Host.Accounts;
using Homespool.Host.Authentication;
using Homespool.Host.Cameras;
using Homespool.Host.Certificates;
using Homespool.Host.Configuration;
using Homespool.Host.Health;
using Homespool.Host.Listeners;
using Homespool.Host.Localisation;
using Homespool.Host.Middleware;
using Homespool.Host.PrusaConnect;
using Homespool.Host.Queue;

namespace Homespool.Host;

public static class Program
{
    public static void Main(string[] args)
    {
        // Answered before anything else starts, because none of them is a server run at all - see
        // StartupApplets for why they precede even the logger, and for the older-image trap they share.
        if (StartupApplets.TryRun(args, out int appletExitCode))
        {
            Environment.ExitCode = appletExitCode;

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
            // preserveStaticLogger keeps this host's logger out of Serilog's process-wide Log.Logger,
            // and it is not a preference. AddSerilog defaults to replacing that static, and then routes
            // every ILogger<T> through it rather than through the instance it just built - so two hosts
            // in one process do not have a logger each. The second to start wins, the first goes on
            // logging into the second's sinks, and nothing anywhere reports a problem.
            //
            // One process runs one host in production, so this was invisible there. It is the test
            // suite that runs hundreds, where the symptom was a host failing to build at all and
            // assertions about log output finding an empty sink - and the workaround was to forbid the
            // suite from starting two hosts at once, which cost it about four minutes a run.
            builder.Services.AddSerilog((services, lc) => lc.ReadFrom.Configuration(builder.Configuration)
                                                                        .ReadFrom.Services(services)
                                                                        .Enrich.FromLogContext()
                                                                        .WriteTo.Console(new RenderedCompactJsonFormatter()),
                                        preserveStaticLogger: true);

            builder.Services.AddHomespoolData(builder.Configuration);

            builder.Services.AddDatabaseDeveloperPageExceptionFilter();

            builder.Services.AddHomespoolDataProtection(builder.Configuration, builder.Environment);

            // This is process-wide and belongs here rather than on a scheme, because what it protects
            // is the DEFAULT for whatever is registered next. AddOidcAuthentication also sets
            // MapInboundClaims = false on its own handler, and that is not redundant with this: this
            // one makes a handler somebody adds later safe without their having to know the rule; that
            // one states it where a reader of the registration can see it. Neither alone is the rule.
            JsonWebTokenHandler.DefaultInboundClaimTypeMap.Clear();

            builder.Services.AddAuthentication(options =>
                            {
                                options.DefaultAuthenticateScheme = IdentityConstants.ApplicationScheme;
                                options.DefaultChallengeScheme = IdentityConstants.ApplicationScheme;
                                options.DefaultSignInScheme = IdentityConstants.ExternalScheme;
                            })
                            .AddIdentityCookieSchemes()
                            .AddPasskeyAuthentication()
                            .AddPrusaConnectPrinterAuthentication()
                            .AddApiTokenAuthentication()
                            .AddXApiKeyAuthentication()
                            .AddOidcAuthentication(builder.Configuration);

            // The framework's AddIdentity, AddEntityFrameworkStores and AddDefaultTokenProviders,
            // written out in IdentityServices so that every service this application authenticates
            // with is declared in code it owns. Its authentication half - the three defaults and the
            // four cookie schemes - is the head of the chain above, where the other schemes already
            // are; SignInManager assumes those schemes exist and does not register them itself.
            builder.Services.AddHomespoolIdentity(Accounts.IdentityConfiguration.Configure)
                            .AddHomespoolStores()
                            .AddErrorDescriber<Accounts.HSIdentityErrorDescriber>()
                            .AddHomespoolTokenProviders();

            // The passkey engine's fixed policy. Its two deployment-bound values come from the scheme
            // registered in the chain above.
            builder.Services.Configure<IdentityPasskeyOptions>(Accounts.IdentityConfiguration.ConfigurePasskeys);

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

            builder.Services.Configure<PrusaConnect.PrinterTrafficLogOptions>(builder.Configuration.GetSection(PrusaConnect.PrinterTrafficLogOptions.SectionName));

            // Singleton because it owns a file handle, and resolved eagerly below so that turning it
            // on is reported at startup rather than whenever the first printer happens to connect.
            builder.Services.AddSingleton<PrusaConnect.PrinterTrafficLog>();

            // StorageOptions is bound by AddHomespoolData, in a project that does not reference the
            // options DataAnnotations extension. The attributes live on the class there; the
            // validator is added here, where the shared framework already carries it.
            builder.Services.AddOptions<StorageOptions>()
                            .ValidateDataAnnotations()
                            .ValidateOnStart();

            builder.Services.Configure<Certificates.CertificateOptions>(builder.Configuration.GetSection(Certificates.CertificateOptions.SectionName));

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

            builder.AddHomespoolListeners();

            builder.AddForwardedHeaders();

            builder.Services.AddOptions<Mail.SmtpOptions>()
                            .Bind(builder.Configuration.GetSection(Mail.SmtpOptions.SectionName))
                            .ValidateDataAnnotations()
                            .ValidateOnStart();

            // The settings file holds one credential, and it is stored encrypted. The post-configure
            // decrypts it into SmtpOptions.Password after binding, so every consumer keeps reading
            // the plain property and none of them knows protection exists.
            builder.Services.AddSingleton<SettingsSecretProtector>();
            builder.Services.AddScoped<SettingsStore>();
            builder.Services.AddSingleton<IPostConfigureOptions<Mail.SmtpOptions>, Mail.SmtpPasswordUnprotector>();

            builder.Services.AddOptions<Accounts.InvitationOptions>()
                            .Bind(builder.Configuration.GetSection(Accounts.InvitationOptions.SectionName))
                            .ValidateDataAnnotations()
                            .ValidateOnStart();

            builder.Services.Configure<Middleware.SecurityOptions>(builder.Configuration.GetSection(Middleware.SecurityOptions.SectionName));

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
            builder.Services.AddSingleton<PrusaConnect.Transfers.ITransferContentStore>(sp => sp.GetRequiredService<PrusaConnect.Transfers.TransferOfferStore>());
            builder.Services.AddSingleton<PrusaConnect.Transfers.ITransferOffers>(sp => sp.GetRequiredService<PrusaConnect.Transfers.TransferOfferStore>());

            // The keys behind encrypted downloads, beside the offers rather than inside them: the
            // store pins bytes and knows nothing of ciphers, which the inline path relies on.
            builder.Services.AddSingleton<PrusaConnect.Transfers.EncryptedTransferOffers>();

            // Uploaded gcode: options, the store, and the content-root accessor it needs. Singleton
            // because the store holds no per-request state - it is a path and a couple of rules.
            builder.Services.AddOptions<PrintFiles.PrintFileStorageOptions>()
                            .Bind(builder.Configuration.GetSection(PrintFiles.PrintFileStorageOptions.SectionName))
                            .ValidateDataAnnotations()
                            .ValidateOnStart();

            builder.Services.AddSingleton<IHostEnvironmentAccessor>(sp => new HostEnvironmentAccessor(sp.GetRequiredService<IWebHostEnvironment>().ContentRootPath));
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

            builder.Services.AddPrinterRateLimiting();

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

            TimeSpan shutdownTimeout = Telemetry.TelemetryWriter.MaxShutdownFlushDuration +
                                       TimeSpan.FromMilliseconds(shutdownStorageOptions.BusyTimeoutMilliseconds) +
                                       TimeSpan.FromSeconds(1.5);

            builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = shutdownTimeout);

            builder.Services.AddHomespoolHealthChecks();

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
            builder.Services.AddScoped<Services.DefaultPrinterService>();
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

            // Point Serilog's process-wide static at the application's own logger, so that anyone who
            // reaches for Log.Error() gets the configured sinks and levels rather than the bootstrap
            // logger, which writes to the console and nowhere else. Without this the static would keep
            // its startup value for the life of the process, and the failure would be silent - the
            // call compiles, logs, and simply never reaches the sink the operator configured.
            //
            // Guarded because a static is only ever right for one owner, and the condition is
            // literally that: one host in this process. Every deployment satisfies it. The test suite
            // does not - it builds hundreds of hosts in one process - and there the static is left
            // alone, because the alternative is hosts overwriting each other's logger, which is the
            // race this guard and preserveStaticLogger above exist to end.
            if (builder.Configuration.GetValue("Diagnostics:OwnsTheStaticLogger", true))
            {
                Log.Logger = app.Services.GetRequiredService<Serilog.ILogger>();
            }

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
            //
            // The lookup here goes through Identity's normaliser, and the stored key was written by
            // whichever normaliser created the row - so the normaliser must never change: a role looked
            // up against a stale key reads as missing, gets created again, and this reopens first-time
            // setup on a running deployment.
            Accounts.AdminBootstrap.SeedAdminBootstrap(app.Services);

            // The certificate nginx presents to printers. Inline, before Run, because the proxy waits
            // on this container's health check and then reads the leaf off the shared volume.
            PrinterCertificateStartup.EnsurePrinterCertificate(app);

            // Resolved here only so that its "this is ON" warning is emitted at startup. Left to the
            // container it would be constructed when the first printer connects, which is both later
            // and buried - and something writing message bodies to disk should say so where an
            // operator scanning a boot log will see it.
            app.Services.GetRequiredService<PrusaConnect.PrinterTrafficLog>();

            // Configure the HTTP request pipeline.
            //
            // The document only. Its viewer is Swagger UI, which is middleware rather than a route and
            // so is registered further down, after the listener boundary that has to cover it.
            if (app.Environment.IsDevelopment())
            {
                app.MapOpenApi().SegregateByListener();
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
                int printerPort = Listeners.ListenerOptions.ReadFrom(builder.Configuration).PrinterPort;
                bool printerListenerIsProxied = PrinterCertificateStartup.PrinterTransportIsSecure(app.Services);

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
            // redirecting at all - see the pinned HttpsPort in Listeners/Registration.cs.
            //
            // Everything except /health. A probe runs inside the container over plain HTTP, and a
            // 307 to https is not a failure to curl - so with redirection applied, a monitoring
            // check would report success without ever reaching the health endpoint. Excluding the
            // path keeps the probe honest wherever TLS is terminated.
            if (Listeners.ListenerOptions.ReadFrom(builder.Configuration).UserHttpsPort is not null)
            {
                app.UseWhen(
                    context => !context.Request.Path.StartsWithSegments(Health.HealthEndpoints.HealthEndpointPath,
                                                                       StringComparison.OrdinalIgnoreCase),
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

            // The viewer for the document mapped above, and where it sits is the whole of its
            // security.
            //
            // Swagger UI serves itself from a static-file branch and publishes no endpoint, so
            // nothing classifies it: not SegregateByListener, which reads route patterns, and not
            // RouteListenerSegregationTests, which enumerates endpoints. Registered above
            // ListenerSegregationMiddleware it would answer on the printer and transfer listeners as
            // well, silently and with the suite still green. Below it, the boundary refuses a
            // /swagger request on those ports by path before this ever runs - so this needs no port
            // check of its own, and neither will the next thing mounted this way.
            //
            // Before the rate limiter and authentication, where the docs endpoint effectively sat
            // before: a Development-only page costs no permit and needs no sign-in.
            if (app.Environment.IsDevelopment())
            {
                app.UseSwaggerUI(options =>
                {
                    // Absolute, because the document is served by MapOpenApi at its own path and not
                    // by this package - a relative URL would resolve under /swagger.
                    options.SwaggerEndpoint("/openapi/v1.json", "Homespool v1");
                    options.DocumentTitle = "Homespool API";
                });
            }

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
            app.UseRequestLocalization(app.Services.GetRequiredService<IOptions<RequestLocalizationOptions>>().Value);

            app.MapHomespoolHealthChecks();

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
}
