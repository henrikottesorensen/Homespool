using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Homespool.Data;
using Homespool.Host.Accounts;
using Homespool.Model.Entities;

namespace Homespool.Host.E2ETest;

/// <summary>
/// Naming a printer as your default, from the two places that offer it.
/// </summary>
/// <remarks>
/// <para>
/// <b>The switch has the same binding trap the remote-ready one has</b>, and for the same reason: a
/// browser posts nothing at all for an unchecked checkbox, so turning the default <em>off</em> rests
/// entirely on an absent field binding to false. A hand-built body cannot prove that, because the
/// test would be deciding what the browser sends.
/// </para>
/// <para>
/// <b>The one case that is this page's own</b> is unchecking the switch on a printer that is not the
/// default. No rendered control can ask for it, a posted form can, and answering it by clearing
/// would throw away a default pointing at a different machine.
/// </para>
/// </remarks>
[Collection("WebApplicationFactory")]
public sealed class DefaultPrinterPageTests : IAsyncLifetime, IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"hs-default-printer-page-{Guid.NewGuid():N}.db");

    private HomespoolFactory _factory = null!;

    public ValueTask InitializeAsync()
    {
        _factory = new HomespoolFactory($"Data Source={_databasePath}");

        _ = _factory.Server;

        using IServiceScope scope = _factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<SetupState>().MarkComplete();

        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        Dispose();

        return ValueTask.CompletedTask;
    }

    public void Dispose()
    {
        _factory.Dispose();

        foreach (string path in new[] { _databasePath, _databasePath + "-wal", _databasePath + "-shm" })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    /// <summary>Ticking the switch stores this printer as the account's default.</summary>
    [Fact]
    public async Task TickingTheSwitchNamesThePrinterAsTheDefault()
    {
        (HSUser user, IReadOnlyList<Printer> printers, HttpClient client) =
            await SeedAsync("default-on@example.com", printerCount: 1);

        using (client)
        {
            string page = await GetDetailAsync(client, printers[0].Uuid);

            HttpResponseMessage posted = await PostSwitchAsync(client, printers[0].Uuid, page,
                                                               new Dictionary<string, string> { ["isDefault"] = "true" });

            posted.StatusCode.Should().Be(HttpStatusCode.Redirect);
            (await ReadDefaultAsync(user.Id)).Should().Be(printers[0].Id);
        }
    }

    /// <summary>
    /// Clearing the switch posts no <c>isDefault</c> field at all, exactly as a browser would, and
    /// the account comes back with no default rather than with the old one.
    /// </summary>
    [Fact]
    public async Task ClearingTheSwitchPostsNothingAndStillRemovesTheDefault()
    {
        (HSUser user, IReadOnlyList<Printer> printers, HttpClient client) =
            await SeedAsync("default-off@example.com", printerCount: 1);

        using (client)
        {
            await SetDefaultAsync(user.Id, printers[0].Id);

            string page = await GetDetailAsync(client, printers[0].Uuid);
            page.Should().Contain("default-printer", "the switch is on the page it acts from");

            // An unchecked checkbox contributes no key, so the form carries the token alone.
            HttpResponseMessage posted = await PostSwitchAsync(client, printers[0].Uuid, page,
                                                               new Dictionary<string, string>());

            posted.StatusCode.Should().Be(HttpStatusCode.Redirect);
            (await ReadDefaultAsync(user.Id)).Should().BeNull("an absent checkbox is how a browser says 'off'");
        }
    }

    /// <summary>
    /// Unchecking the switch on a printer that is not the default leaves the real default alone.
    /// </summary>
    /// <remarks>
    /// The rendered page never asks for this - the switch is already unticked there - but a posted
    /// form can, and clearing on it would silently drop a choice pointing at another machine.
    /// </remarks>
    [Fact]
    public async Task UncheckingOnAnotherPrinterLeavesTheDefaultAlone()
    {
        (HSUser user, IReadOnlyList<Printer> printers, HttpClient client) =
            await SeedAsync("default-other@example.com", printerCount: 2);

        using (client)
        {
            await SetDefaultAsync(user.Id, printers[0].Id);

            string page = await GetDetailAsync(client, printers[1].Uuid);

            HttpResponseMessage posted = await PostSwitchAsync(client, printers[1].Uuid, page,
                                                               new Dictionary<string, string>());

            posted.StatusCode.Should().Be(HttpStatusCode.Redirect);
            (await ReadDefaultAsync(user.Id)).Should().Be(
                printers[0].Id, "turning off a switch that was already off changes nothing");
        }
    }

    /// <summary>
    /// The listing says which printer is the default and offers the change on the others only.
    /// </summary>
    [Fact]
    public async Task TheListingMarksTheDefaultAndOffersToChangeItElsewhere()
    {
        (HSUser user, IReadOnlyList<Printer> printers, HttpClient client) =
            await SeedAsync("default-listing@example.com", printerCount: 2);

        using (client)
        {
            await SetDefaultAsync(user.Id, printers[0].Id);

            string listing =
                await (await client.GetAsync("/Printers", TestContext.Current.CancellationToken)).Content
                    .ReadAsStringAsync(TestContext.Current.CancellationToken);

            listing.Should().Contain($"printerId={printers[1].Id}",
                                     "the other printer is the one that can still be made the default");
            listing.Should().NotContain($"handler=Default&amp;printerId={printers[0].Id}",
                                        "the printer that already is it has nothing to offer");
        }
    }

    /// <summary>
    /// The listing's button is what an account uses to change its default, and it takes effect.
    /// </summary>
    [Fact]
    public async Task TheListingsButtonChangesTheDefault()
    {
        (HSUser user, IReadOnlyList<Printer> printers, HttpClient client) =
            await SeedAsync("default-switching@example.com", printerCount: 2);

        using (client)
        {
            await SetDefaultAsync(user.Id, printers[0].Id);

            string listing =
                await (await client.GetAsync("/Printers", TestContext.Current.CancellationToken)).Content
                    .ReadAsStringAsync(TestContext.Current.CancellationToken);

            using FormUrlEncodedContent body = new(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = AntiforgeryTestHelper.ExtractToken(listing),
            });

            using HttpResponseMessage posted = await client.PostAsync(
                $"/Printers?handler=Default&printerId={printers[1].Id}", body, TestContext.Current.CancellationToken);

            posted.StatusCode.Should().Be(HttpStatusCode.Redirect);
            (await ReadDefaultAsync(user.Id)).Should().Be(printers[1].Id);
        }
    }

    /// <summary>
    /// A printer on somebody else's team is refused, and the caller's own default survives it.
    /// </summary>
    [Fact]
    public async Task APrinterTheCallerCannotSeeIsRefused()
    {
        (HSUser user, IReadOnlyList<Printer> printers, HttpClient client) =
            await SeedAsync("default-stranger@example.com", printerCount: 1);

        (HSUser _, IReadOnlyList<Printer> theirs, HttpClient other) =
            await SeedAsync("default-owner@example.com", printerCount: 1);

        using (client)
        using (other)
        {
            await SetDefaultAsync(user.Id, printers[0].Id);

            string listing =
                await (await client.GetAsync("/Printers", TestContext.Current.CancellationToken)).Content
                    .ReadAsStringAsync(TestContext.Current.CancellationToken);

            using FormUrlEncodedContent body = new(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = AntiforgeryTestHelper.ExtractToken(listing),
            });

            using HttpResponseMessage posted = await client.PostAsync(
                $"/Printers?handler=Default&printerId={theirs[0].Id}", body, TestContext.Current.CancellationToken);

            posted.StatusCode.Should().Be(HttpStatusCode.Redirect);
            (await ReadDefaultAsync(user.Id)).Should().Be(
                printers[0].Id, "a refusal must not take the caller's own choice with it");
        }
    }

    private async Task<string> GetDetailAsync(HttpClient client, Guid uuid)
    {
        using HttpResponseMessage response = await client.GetAsync($"/Printers/Detail/{uuid}",
                                                                   TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        return await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
    }

    private async Task<HttpResponseMessage> PostSwitchAsync(HttpClient client,
                                                            Guid uuid,
                                                            string page,
                                                            Dictionary<string, string> fields)
    {
        fields["__RequestVerificationToken"] = AntiforgeryTestHelper.ExtractToken(page);

        using FormUrlEncodedContent body = new(fields);

        return await client.PostAsync($"/Printers/Detail/{uuid}?handler=Default", body,
                                      TestContext.Current.CancellationToken);
    }

    private async Task<int?> ReadDefaultAsync(long userId)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        HomespoolDbContext context = scope.ServiceProvider.GetRequiredService<HomespoolDbContext>();

        HSUser stored = await context.Users.AsNoTracking()
                                     .SingleAsync(u => u.Id == userId, TestContext.Current.CancellationToken);

        return stored.DefaultPrinterId;
    }

    /// <summary>
    /// Writes the column directly, so a test about one surface does not depend on the other working.
    /// </summary>
    private async Task SetDefaultAsync(long userId, int printerId)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        HomespoolDbContext context = scope.ServiceProvider.GetRequiredService<HomespoolDbContext>();

        HSUser stored = await context.Users.SingleAsync(u => u.Id == userId, TestContext.Current.CancellationToken);
        stored.DefaultPrinterId = printerId;

        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private async Task<(HSUser user, IReadOnlyList<Printer> printers, HttpClient client)> SeedAsync(
        string email, int printerCount)
    {
        (HSUser user, HttpClient client) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(_factory, email);

        using IServiceScope scope = _factory.Services.CreateScope();
        HomespoolDbContext context = scope.ServiceProvider.GetRequiredService<HomespoolDbContext>();

        TeamMember membership = await context.TeamMembers
                                             .SingleAsync(m => m.UserId == user.Id, TestContext.Current.CancellationToken);

        List<Printer> printers = [];

        for (int i = 0; i < printerCount; i++)
        {
            Printer printer = new()
            {
                Uuid = Guid.NewGuid(),
                TeamId = membership.TeamId,
                Name = $"Bench {i}",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            };

            context.Printers.Add(printer);
            printers.Add(printer);
        }

        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        return (user, printers, client);
    }
}
