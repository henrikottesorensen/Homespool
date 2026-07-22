using System;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.JsonWebTokens;

using PrinterService.Api.Authentication;
using PrinterService.Data;

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
            // Microsoft made a decision back in the day to remap all claims to their equivalent WS-Identity url identifier.
            // So the JWT 'name' claim becomes http://schemas.xmlsoap.org/ws/2005/05/identity/claims/name in the ClaimsPrincipal.
            // This method disables remapping.
            JsonWebTokenHandler.DefaultInboundClaimTypeMap.Clear();

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
            
            builder.Services.AddControllers();

            builder.Services.AddOpenApi();

            builder.Services.AddScoped<Services.IEmailSender, Services.LoggingEmailSender>();

            builder.Services.AddScoped<PrusaConnect.PrusaConnectService>()
                            .AddScoped<PrusaConnect.WebSocketHandler>()
                            .AddScoped<PrusaConnect.TokenService>()
                            .AddScoped<PrusaConnect.CodeGenerator>();
            
            WebApplication app = builder.Build();

            app.Services.MigratePrinterServiceData();

            // Configure the HTTP request pipeline.
            if (app.Environment.IsDevelopment())
            {
                app.MapOpenApi();
            }
            app.UseSerilogRequestLogging();
            
            app.UseHttpsRedirection();

            app.UseRouting();

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
