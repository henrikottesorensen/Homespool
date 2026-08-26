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
    /// <b>The form opens with nothing ticked.</b> What somebody does not think about is what the
    /// token cannot do, so every capability it carries is one a person chose.
    /// </summary>
    [Fact]
    public async Task TheFormOpensWithNothingTicked()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        (ApiTokensModel model, _) = await NewModelAsync(context, string.Empty);

        // Act
        await model.OnGetAsync(TestContext.Current.CancellationToken);

        // Assert
        model.Input.Scope.Should().BeEmpty();
    }

    /// <summary>
    /// Minting one leaves the next form empty rather than pre-ticked with what was just granted -
    /// otherwise the second token quietly defaults to the first one's rights.
    /// </summary>
    [Fact]
    public async Task TheFormAfterMintingDoesNotCarryTheScopeForward()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        (ApiTokensModel model, _) = await NewModelAsync(context, "laptop");
        model.Input.Scope = [Capability.ManagePrinter, Capability.ManipulateOwnFiles];

        // Act
        await model.OnPostAsync(TestContext.Current.CancellationToken);

        // Assert
        model.CreatedToken.Should().NotBeNull("this one was minted");
        model.Input.Scope.Should().BeEmpty("the next token starts from nothing, like the first");
    }

    /// <summary>
    /// Tick all is the way back for somebody who wants everything, so the empty default costs a click
    /// rather than nine.
    /// </summary>
    [Fact]
    public async Task TickingAllSelectsEveryCapability()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        (ApiTokensModel model, _) = await NewModelAsync(context, "everything");
        model.Input.Scope = [];

        // Act
        await model.OnPostTickAllAsync(TestContext.Current.CancellationToken);

        // Assert
        model.Input.Scope.Should().BeEquivalentTo(CapabilitySet.Everything);
        model.CreatedToken.Should().BeNull("ticking is not minting");
        context.ApiTokens.Should().BeEmpty();
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

    /// <summary>
    /// <b>Untick all clears every box and mints nothing.</b> The no-script path through the button:
    /// the form comes back empty-scoped, ready to be ticked up from nothing.
    /// </summary>
    [Fact]
    public async Task UntickingAllClearsEveryBox()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        (ApiTokensModel model, _) = await NewModelAsync(context, "slicer");

        // Act
        await model.OnPostUntickAllAsync(TestContext.Current.CancellationToken);

        // Assert
        model.Input.Scope.Should().BeEmpty();
        model.CreatedToken.Should().BeNull("unticking is not minting");
        context.ApiTokens.Should().BeEmpty();
    }

    /// <summary>
    /// It keeps what was typed, and says nothing about what has not been. Pressing it is a step
    /// towards filling the form in, so reporting the empty name as an error would be scolding
    /// somebody for a step they have not reached.
    /// </summary>
    [Fact]
    public async Task UntickingAllKeepsTheNameAndComplainsAboutNothing()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        (ApiTokensModel model, _) = await NewModelAsync(context, string.Empty);

        // The state the button is actually pressed in: binding has run against an unfinished form,
        // so both validation failures are already sitting in ModelState.
        model.Input.Name = "half-typed";
        model.ModelState.AddModelError("Input.Name", "The Name field is required.");
        model.ModelState.AddModelError("Input.Scope", "Tokens_ScopeRequired");

        // Act
        await model.OnPostUntickAllAsync(TestContext.Current.CancellationToken);

        // Assert
        model.Input.Name.Should().Be("half-typed", "a round trip must not cost what was typed");
        model.ModelState.ErrorCount.Should().Be(0, "nobody has tried to mint anything yet");
    }

    /// <summary>
    /// Unticking everything and then submitting is still refused - the button reaches a state the
    /// form declines to mint from, and that is deliberate rather than an oversight.
    /// </summary>
    [Fact]
    public async Task UntickingAllStillLeavesAFormThatWillNotMint()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        (ApiTokensModel model, _) = await NewModelAsync(context, "useless");
        await model.OnPostUntickAllAsync(TestContext.Current.CancellationToken);

        // Act
        model.ModelState.AddModelError("Input.Scope", "Tokens_ScopeRequired");
        await model.OnPostAsync(TestContext.Current.CancellationToken);

        // Assert
        context.ApiTokens.Should().BeEmpty("an empty scope is representable, not mintable");
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
