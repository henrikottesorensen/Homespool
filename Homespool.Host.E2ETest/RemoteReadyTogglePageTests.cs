using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Homespool.Data;
using Homespool.Host.Services;
using Homespool.Model.Entities;

namespace Homespool.Host.E2ETest;

/// <summary>
/// The remote-ready switch, driven the way a browser drives it.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the test the unit ones cannot be.</b> <c>RemoteReadyAllowedTests</c> calls the service
/// with a <see cref="bool"/> already decided, so nothing there touches the step that actually
/// carries the answer: <b>a browser posts nothing at all for an unchecked checkbox.</b> Turning the
/// switch off therefore depends entirely on an absent field binding to <c>false</c>, and no unit
/// test can observe that because model binding is the thing under test.
/// </para>
/// <para>
/// The failure it guards is silent and one-directional. If the binding were wrong the switch would
/// only ever turn <em>on</em> - a manager locking a printer down would see the page come back saying
/// it was done, with the flag still set. There is no error, no exception, and the only symptom is a
/// printer that stays readable from a sofa after somebody decided it should not be.
/// </para>
/// <para>
/// The markup deliberately carries no companion hidden field, which is what makes the absence real:
/// the tag helper form of a checkbox emits one so the value is always present, and this form does
/// not use it.
/// </para>
/// </remarks>
[Collection("WebApplicationFactory")]
public sealed class RemoteReadyTogglePageTests : IAsyncLifetime, IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"hs-remote-ready-page-{Guid.NewGuid():N}.db");

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

    /// <summary>
    /// Ticking the switch posts <c>allowed=true</c> and the flag is set.
    /// </summary>
    [Fact]
    public async Task TickingTheSwitchAllowsIt()
    {
        // Arrange
        (Guid uuid, HttpClient client) = await SeedPrinterAsync("remote-ready-on@example.com", allowed: false);

        using (client)
        {
            string page = await GetDetailAsync(client, uuid);

            // Act
            HttpResponseMessage posted = await PostToggleAsync(client, uuid, page,
                                                               new Dictionary<string, string> { ["allowed"] = "true" });

            // Assert
            posted.StatusCode.Should().Be(HttpStatusCode.Redirect);
            (await ReadFlagAsync(uuid)).Should().BeTrue();
        }
    }

    /// <summary>
    /// <b>The one that matters.</b> Clearing the switch posts <em>no</em> <c>allowed</c> field at
    /// all, exactly as a browser would, and the flag must come back false.
    /// </summary>
    [Fact]
    public async Task ClearingTheSwitchPostsNothingAndStillTurnsItOff()
    {
        // Arrange
        (Guid uuid, HttpClient client) = await SeedPrinterAsync("remote-ready-off@example.com", allowed: true);

        using (client)
        {
            string page = await GetDetailAsync(client, uuid);
            page.Should().Contain("checked", "a printer that allows it renders the switch already on");

            // The half a hand-built POST body cannot prove. This test decides for itself what an
            // unchecked box sends, so without this the markup could grow a companion hidden field -
            // the tag-helper default - and every assertion below would still pass while the real
            // page had stopped being able to turn the switch off at all.
            Regex.IsMatch(page, """<input[^>]*type="hidden"[^>]*name="allowed""")
                 .Should().BeFalse("a companion hidden field would make the checkbox unable to say 'off'");

            // Act - an unchecked checkbox contributes no key, so the form carries the token alone.
            HttpResponseMessage posted = await PostToggleAsync(client, uuid, page, new Dictionary<string, string>());

            // Assert
            posted.StatusCode.Should().Be(HttpStatusCode.Redirect);
            (await ReadFlagAsync(uuid)).Should().BeFalse("an absent checkbox is how a browser says 'off'");
        }
    }

    /// <summary>
    /// The Save button is in the markup rather than added by script, so the form still works with
    /// scripting off - which is what makes hiding it an enhancement rather than the control.
    /// </summary>
    [Fact]
    public async Task TheFormCarriesItsOwnSubmitButtonForBrowsersWithoutScripting()
    {
        (Guid uuid, HttpClient client) = await SeedPrinterAsync("remote-ready-noscript@example.com", allowed: false);

        using (client)
        {
            string page = await GetDetailAsync(client, uuid);

            page.Should().Contain("data-submit-fallback", "the no-script path is served, not scripted in");
            page.Should().Contain("data-submit-on-change", "and the switch is marked for the enhancement");
        }
    }

    private async Task<string> GetDetailAsync(HttpClient client, Guid uuid)
    {
        using HttpResponseMessage response = await client.GetAsync($"/Printers/Detail/{uuid}",
                                                                   TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        return await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
    }

    private async Task<HttpResponseMessage> PostToggleAsync(HttpClient client,
                                                            Guid uuid,
                                                            string page,
                                                            Dictionary<string, string> fields)
    {
        fields["__RequestVerificationToken"] = AntiforgeryTestHelper.ExtractToken(page);

        using FormUrlEncodedContent body = new(fields);

        return await client.PostAsync($"/Printers/Detail/{uuid}?handler=RemoteReady", body,
                                      TestContext.Current.CancellationToken);
    }

    private async Task<bool> ReadFlagAsync(Guid uuid)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        HomespoolDbContext context = scope.ServiceProvider.GetRequiredService<HomespoolDbContext>();

        Printer printer = await context.Printers
                                       .AsNoTracking()
                                       .SingleAsync(p => p.Uuid == uuid, TestContext.Current.CancellationToken);

        return printer.RemoteReadyAllowed;
    }

    private async Task<(Guid uuid, HttpClient client)> SeedPrinterAsync(string email, bool allowed)
    {
        (HSUser user, HttpClient client) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(_factory, email);

        Guid uuid = Guid.NewGuid();

        using IServiceScope scope = _factory.Services.CreateScope();
        HomespoolDbContext context = scope.ServiceProvider.GetRequiredService<HomespoolDbContext>();

        TeamMember membership = await context.TeamMembers
                                             .SingleAsync(m => m.UserId == user.Id, TestContext.Current.CancellationToken);

        membership.CanManage.Should().BeTrue("the default team makes its creator a manager, which this needs");

        context.Printers.Add(new Printer
        {
            Uuid = uuid,
            TeamId = membership.TeamId,
            Name = "Garage MINI",
            RemoteReadyAllowed = allowed,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });

        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        return (uuid, client);
    }
}
