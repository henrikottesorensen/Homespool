using System;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using PrinterService.Api.Authentication;
using PrinterService.Data;

using Scalar.AspNetCore;

using Serilog;
using Serilog.Events;
using Serilog.Formatting.Compact;

namespace PrinterService.Api;

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

            Services.SmtpOptions smtpOptions = new();
            builder.Configuration.GetSection(Services.SmtpOptions.SectionName).Bind(smtpOptions);

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
                            .AddScoped<PrusaConnect.CodeGenerator>();
            
            WebApplication app = builder.Build();

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
            
            app.UseHttpsRedirection();

            app.UseRouting();

            // Before an administrator exists, funnel every navigable page to /setup. No-op once setup
            // completes. Placed after routing so static-asset and printer endpoints resolve normally.
            app.UseMiddleware<Services.SetupGateMiddleware>();

            app.UseAuthentication();
            app.UseAuthorization();

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
}
