using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

using Homespool.Data;
using Homespool.Host.Pages.Account.Manage;
using Homespool.Host.Services;
using Homespool.Model.Entities;

namespace Homespool.Host.Test;

/// <summary>
/// The page that mints personal access tokens - specifically the handling of the one response that
/// ever contains a secret.
/// </summary>
public sealed class ApiTokensPageTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"hs-apipage-{Guid.NewGuid():N}.db");

    private HSDbContext NewContext()
    {
        DbContextOptions<HSDbContext> options = new DbContextOptionsBuilder<HSDbContext>()
            .UseSqlite($"Data Source={_databasePath}")
            .Options;

        return new HSDbContext(options);
    }

    private async Task<HSDbContext> MigratedContextAsync()
    {
        HSDbContext context = NewContext();
        await context.Database.MigrateAsync(TestContext.Current.CancellationToken);

        return context;
    }

    public void Dispose()
    {
        foreach (string path in new[] { _databasePath, _databasePath + "-wal", _databasePath + "-shm" })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    private static async Task<(ApiTokensModel model, DefaultHttpContext httpContext)> NewModelAsync(HSDbContext context, string name)
    {
        (UserManager<HSUser> users, _, DefaultHttpContext httpContext, _) =
            IdentityTestHarness.BuildIdentityServices(context);

        HSUser user = new("owner@example.com") { Email = "owner@example.com" };
        (await users.CreateAsync(user)).Succeeded.Should().BeTrue();
        IdentityTestHarness.SignInAsPrincipal(httpContext, user);

        ApiTokensModel model = new(new ApiTokenService(context), users, NullLogger<ApiTokensModel>.Instance)
        {
            PageContext = IdentityTestHarness.NewPageContext(httpContext),
            Input = new ApiTokensModel.InputModel { Name = name },
        };

        return (model, httpContext);
    }

    /// <summary>
    /// The response carrying the one-time secret says <c>no-store</c>. POST responses are not
    /// cacheable anyway, so the case this closes is the back/forward cache putting the secret back on
    /// screen after the fact.
    /// </summary>
    [Fact]
    public async Task TheResponseCarryingANewSecretIsNotStorable()
    {
        // Arrange
        await using HSDbContext context = await MigratedContextAsync();
        (ApiTokensModel model, DefaultHttpContext httpContext) = await NewModelAsync(context, "laptop");

        // Act
        await model.OnPostAsync(CancellationToken.None);

        // Assert
        model.CreatedToken.Should().NotBeNull("this is the response that carries the secret");
        httpContext.Response.Headers.CacheControl.ToString().Should().Be("no-store");
    }

    /// <summary>
    /// The secret is shown once and the name field is cleared, so a reload does not re-post the same
    /// name back - and the new token is in the list underneath it.
    /// </summary>
    [Fact]
    public async Task CreatingATokenShowsItOnceAndListsIt()
    {
        // Arrange
        await using HSDbContext context = await MigratedContextAsync();
        (ApiTokensModel model, _) = await NewModelAsync(context, "laptop");

        // Act
        await model.OnPostAsync(CancellationToken.None);

        // Assert
        model.CreatedToken.Should().StartWith(ApiTokenService.Prefix);
        model.Input.Name.Should().BeEmpty();
        model.Tokens.Should().ContainSingle().Which.Name.Should().Be("laptop");
    }
}
