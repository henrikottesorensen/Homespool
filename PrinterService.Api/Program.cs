using System;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

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
            WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

            // Add services to the container.
            builder.Services.AddSerilog((services, lc) => lc
                   .ReadFrom.Configuration(builder.Configuration)
                   .ReadFrom.Services(services)
                   .Enrich.FromLogContext()
                   .WriteTo.Console(new RenderedCompactJsonFormatter()));
            
            builder.Services.AddDbContext<PSDbContext>(ef =>
            {
                ef.UseSqlite(builder.Configuration.GetConnectionString("PrinterServiceDb"));
            });
            
            builder.Services.AddDatabaseDeveloperPageExceptionFilter();
            
            builder.Services.AddDataProtection()
                            .PersistKeysToDbContext<PSDbContext>();
            
            builder.Services.AddAuthentication()
                            .AddPrusaConnectPrinterAuthentication();

            builder.Services.AddDefaultIdentity<Model.Entities.PSUser>(options => options.SignIn.RequireConfirmedAccount = true)
                            .AddEntityFrameworkStores<PSDbContext>();
            
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

            builder.Services.AddScoped<PrusaConnect.PrusaConnectService>()
                            .AddScoped<PrusaConnect.WebSocketHandler>()
                            .AddScoped<PrusaConnect.TokenService>()
                            .AddScoped<PrusaConnect.CodeGenerator>();
            
            WebApplication app = builder.Build();

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
