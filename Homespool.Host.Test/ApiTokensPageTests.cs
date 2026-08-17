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
using Homespool.Host.Localisation;
using Homespool.Host.Pages.Account.Manage;
using Homespool.Host.Services;
using Homespool.Model;
using Homespool.Model.Entities;

namespace Homespool.Host.Test;

/// <summary>
/// The page that mints personal access tokens - specifically the handling of the one response that
/// ever contains a secret.
/// </summary>
public sealed class ApiTokensPageTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"hs-apipage-{Guid.NewGuid():N}.db");

    private HomespoolDbContext NewContext()
    {
        DbContextOptions<HomespoolDbContext> options = new DbContextOptionsBuilder<HomespoolDbContext>()
                                                       .UseSqlite($"Data Source={_databasePath}")
                                                       .Options;

        return new HomespoolDbContext(options);
    }

    private async Task<HomespoolDbContext> MigratedContextAsync()
    {
        HomespoolDbContext context = NewContext();
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

    /// <summary>
    /// <b>The form mints what the boxes say.</b> A person narrowing the scope gets a token that
    /// carries exactly that, which is the whole feature.
    /// </summary>
    [Fact]
    public async Task CreatingATokenStoresTheScopeThatWasTicked()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        (ApiTokensModel model, _) = await NewModelAsync(context, "slicer");
        model.Input.Scope = [Capability.UploadOwnFiles, Capability.Print];

        // Act
        await model.OnPostAsync(TestContext.Current.CancellationToken);

        // Assert
        ApiToken token = await context.ApiTokens.SingleAsync(TestContext.Current.CancellationToken);
        CapabilitySet stored = CapabilitySet.Parse(token.Scope);

        stored.Allows(Capability.UploadOwnFiles).Should().BeTrue();
        stored.Allows(Capability.Print).Should().BeTrue();
        stored.Allows(Capability.ViewPrinter).Should().BeTrue("Print implies it, and Format closes the set");
        stored.Allows(Capability.ManipulateOwnFiles).Should().BeFalse("nobody ticked it");
        stored.Allows(Capability.ManagePrinter).Should().BeFalse("nor that");
    }

    /// <summary>
    /// The form opens with everything ticked, so narrowing is a deliberate act rather than a chore -
    /// and so the default is the credential somebody expects.
    /// </summary>
    [Fact]
    public async Task TheFormOpensWithEverythingTicked()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        (ApiTokensModel model, _) = await NewModelAsync(context, string.Empty);

        // Act
        await model.OnGetAsync(TestContext.Current.CancellationToken);

        // Assert
        model.Input.Scope.Should().BeEquivalentTo(CapabilitySet.Everything);
    }

    /// <summary>
    /// <b>Unticking everything is refused, though an empty scope is representable on purpose.</b> The
    /// model must be able to say "this token can do nothing" - that is what keeps empty from being
    /// overloaded to mean unrestricted - but nobody arrives at this form intending to mint one.
    /// </summary>
    [Fact]
    public async Task ATokenWithNothingTickedIsRefused()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        (ApiTokensModel model, _) = await NewModelAsync(context, "useless");
        model.Input.Scope = [];
        model.ModelState.AddModelError("Input.Scope", "Tokens_ScopeRequired");

        // Act
        await model.OnPostAsync(TestContext.Current.CancellationToken);

        // Assert
        context.ApiTokens.Should().BeEmpty("an invalid form mints nothing");
        model.CreatedToken.Should().BeNull();
    }

    private static async Task<(ApiTokensModel model, DefaultHttpContext httpContext)> NewModelAsync(
        HomespoolDbContext context,
        string name)
    {
        (UserManager<HSUser> users, _, DefaultHttpContext httpContext, _) =
            IdentityTestHarness.BuildIdentityServices(context);

        HSUser user = new("owner") { Email = "owner@example.com" };
        (await users.CreateAsync(user)).Succeeded.Should().BeTrue();
        IdentityTestHarness.SignInAsPrincipal(httpContext, user);

        ApiTokensModel model = new(new ApiTokenService(context), users, NullLogger<ApiTokensModel>.Instance,
                                   TestLocaliser.Shared(), new CapabilityText(TestLocaliser.Shared()))
        {
            PageContext = IdentityTestHarness.NewPageContext(httpContext),
            Input = new ApiTokensModel.InputModel
            {
                Name = name,
                Scope = [.. CapabilitySet.Everything],
            },
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
        await using HomespoolDbContext context = await MigratedContextAsync();
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
        await using HomespoolDbContext context = await MigratedContextAsync();
        (ApiTokensModel model, _) = await NewModelAsync(context, "laptop");

        // Act
        await model.OnPostAsync(CancellationToken.None);

        // Assert
        model.CreatedToken.Should().StartWith(ApiTokenService.Prefix);
        model.Input.Name.Should().BeEmpty();
        model.Tokens.Should().ContainSingle().Which.Name.Should().Be("laptop");
    }
}
