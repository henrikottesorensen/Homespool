using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using PrinterService.Host.Authentication;
using PrinterService.Data;

using Scalar.AspNetCore;

using Serilog;
using Serilog.Events;
using Serilog.Formatting.Compact;

namespace PrinterService.Host;

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
            
            builder.Services.AddPrinterServiceData(builder.Configuration);

            builder.Services.AddDatabaseDeveloperPageExceptionFilter();
            
            builder.Services.AddDataProtection()
                            .PersistKeysToDbContext<PSDbContext>();
            
            builder.Services.AddAuthentication()
                            .AddPrusaConnectPrinterAuthentication();

            builder.Services.AddIdentity<Model.Entities.PSUser, IdentityRole<long>>(options => options.SignIn.RequireConfirmedAccount = true)
                            .AddEntityFrameworkStores<PSDbContext>()
                            .AddDefaultTokenProviders();
            
            builder.Services.ConfigureApplicationCookie(options =>
            {
                // Cookie settings
                options.Cookie.HttpOnly = true;
                options.ExpireTimeSpan = TimeSpan.FromDays(1);

                options.LoginPath = "/Account/Login";
                options.AccessDeniedPath = "/Account/AccessDenied";
                options.SlidingExpiration = true;
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
            // none of these three touch PSDbContext, only in-memory maps (live connections, pending
            // command replies), so there is no scoped dependency to protect against capturing.
            builder.Services.AddSingleton<PrusaConnect.PrinterConnectionRegistry>();
            builder.Services.AddSingleton<PrusaConnect.IPrinterCommandCorrelator, PrusaConnect.PrinterCommandCorrelator>();
            builder.Services.AddSingleton<PrusaConnect.IPrinterCommandTransport, PrusaConnect.PrinterCommandTransport>();

            // Singleton, not scoped like its neighbors above: one drain loop and one in-memory
            // live-state cache for the whole process, fed by every request's scoped
            // MessageDispatcher through the ITelemetrySink interface - so a request never hands
            // the writer its own PSDbContext, only a DTO.
            //
            // The writer still needs PSDbContext to persist, which is the usual trap for a
            // singleton: inject the scoped context directly and it gets captured once, reused
            // forever, single-threaded and stale, for the life of the process. TelemetryWriter
            // avoids this by injecting IServiceScopeFactory instead - itself a singleton, safe to
            // hold - and calling CreateScope() fresh in HydrateAsync and FlushAsync, each wrapped
            // in a `using` that disposes the scope (and its PSDbContext) the moment that one
            // read or write finishes. No PSDbContext field ever exists on TelemetryWriter itself.
            builder.Services.AddSingleton<PrusaConnect.TelemetryWriter>();
            builder.Services.AddSingleton<PrusaConnect.ITelemetrySink>(sp => sp.GetRequiredService<PrusaConnect.TelemetryWriter>());
            builder.Services.AddSingleton<PrusaConnect.ITelemetryHealthSource>(sp => sp.GetRequiredService<PrusaConnect.TelemetryWriter>());
            builder.Services.AddHostedService(sp => sp.GetRequiredService<PrusaConnect.TelemetryWriter>());

            // The process answering requests says nothing about whether it is still recording
            // anything - a flush bug once made every write fail permanently while the service looked
            // entirely healthy from outside. This is the hook a monitoring system can watch.
            builder.Services.AddHealthChecks()
                   .AddCheck<PrusaConnect.TelemetryPersistenceHealthCheck>("telemetry-persistence");

            // Sweeps TelemetrySample rows past StorageOptions.TelemetryRetentionDays. No interface
            // registration needed, unlike TelemetryWriter above - nothing else ever needs to reach it.
            builder.Services.AddHostedService<Services.TelemetryRetentionService>();

            // Scoped, unlike their singleton neighbors above, because they hold the scoped PSDbContext.
            builder.Services.AddScoped<Services.TeamService>();
            builder.Services.AddScoped<Services.UnitOfWork>();
            builder.Services.AddScoped<Services.InvitationService>();
            builder.Services.AddScoped<Services.PrinterQueryService>();
            
            WebApplication app = builder.Build();

            // Ctrl-C/SIGTERM is otherwise silent: the framework's own "Application is shutting
            // down..." comes from Microsoft.Hosting.Lifetime, and Serilog's Microsoft override
            // (appsettings.json) filters that namespace to Warning. An operator watching a blank
            // console while telemetry drains has no way to tell progress from a hang, and reaches
            // for SIGKILL - which is exactly what loses the buffered samples. TelemetryWriter logs
            // the matching "drained" or "unwritten" line when it finishes.
            app.Lifetime.ApplicationStopping.Register(() =>
                app.Logger.LogInformation("Shutting down: draining buffered telemetry to the database. Please let this finish."));

            app.Services.MigratePrinterServiceData();

            // Ensure the admin role exists and, if no administrator has been created yet, mint and log
            // the one-time /setup token. Runs inline so setup state is settled before the first request.
            Services.AdminBootstrap.SeedAdminBootstrap(app.Services);

            // Configure the HTTP request pipeline.
            if (app.Environment.IsDevelopment())
            {
                app.MapOpenApi();
                app.MapScalarApiReference();
            }
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

            app.UseAuthentication();
            app.UseAuthorization();

            // Anonymous by design: a monitoring system holds no credentials, and the response carries
            // only counters and timestamps about this service's own write path - nothing about
            // printers, jobs or users.
            app.MapHealthChecks(HealthEndpointPath, new HealthCheckOptions
            {
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

    /// <summary>Where the health endpoint lives. Shared so the HTTPS-redirection exclusion above
    /// cannot drift away from the route itself.</summary>
    private const string HealthEndpointPath = "/health";

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
