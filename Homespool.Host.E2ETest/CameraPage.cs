using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;

using Homespool.Data;
using Homespool.Host.Controllers;
using Homespool.Host.Localisation;
using Homespool.Model.Entities;

namespace Homespool.Host.E2ETest;

/// <summary>
/// Driving the real Cameras page as a browser would: adding, changing and removing a camera, and
/// reading what the page said about it afterwards.
/// </summary>
public static class CameraPage
{
    /// <summary>
    /// Adds a network camera to the account's default team through the page, bound to a printer when
    /// one is given, and returns it as stored.
    /// </summary>
    public static async Task<Camera> AddNetworkCameraAsync(WebApplicationFactory<PrinterAppController> factory,
                                                           HttpClient client,
                                                           HSUser user,
                                                           string name,
                                                           string source,
                                                           Guid? printerUuid = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(user);

        string page = await GetAsync(client, "/Cameras");
        Guid teamUuid = await TeamUuidOfAsync(factory, user);

        using FormUrlEncodedContent form = new(
        [
            new("__RequestVerificationToken", AntiforgeryTestHelper.ExtractToken(page)),
            new("name", name),
            new("source", source),
            new("teamUuid", teamUuid.ToString()),
            new("printerUuid", printerUuid?.ToString() ?? string.Empty),
        ]);

        using HttpResponseMessage response =
            await client.PostAsync("/Cameras?handler=AddNetwork", form, TestContext.Current.CancellationToken);

        using IServiceScope scope = factory.Services.CreateScope();
        HomespoolDbContext context = scope.ServiceProvider.GetRequiredService<HomespoolDbContext>();

        return await context.Cameras
                            .AsNoTracking()
                            .SingleAsync(camera => camera.Name == name, TestContext.Current.CancellationToken);
    }

    /// <summary>Changes a network camera's name and source through the page, unbound from any printer.</summary>
    public static async Task EditAsync(HttpClient client, Guid uuid, string name, string source)
    {
        ArgumentNullException.ThrowIfNull(client);

        string page = await GetAsync(client, $"/Cameras?edit={uuid}");

        using FormUrlEncodedContent form = new(
        [
            new("__RequestVerificationToken", AntiforgeryTestHelper.ExtractToken(page)),
            new("uuid", uuid.ToString()),
            new("name", name),
            new("source", source),
            new("printerUuid", string.Empty),
        ]);

        using HttpResponseMessage response =
            await client.PostAsync("/Cameras?handler=Edit", form, TestContext.Current.CancellationToken);
    }

    /// <summary>Removes a camera through the page.</summary>
    public static async Task DeleteAsync(HttpClient client, Guid uuid)
    {
        ArgumentNullException.ThrowIfNull(client);

        string page = await GetAsync(client, "/Cameras");

        using FormUrlEncodedContent form = new(
        [
            new("__RequestVerificationToken", AntiforgeryTestHelper.ExtractToken(page)),
            new("uuid", uuid.ToString()),
        ]);

        using HttpResponseMessage response =
            await client.PostAsync("/Cameras?handler=Delete", form, TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// What the page said after the last post, as the alert of the given kind - <c>success</c>,
    /// <c>warning</c> or <c>danger</c> - carries it, or empty when there is none.
    /// </summary>
    /// <remarks>
    /// The message rides in TempData across the redirect, so this is the one read that sees it:
    /// asking twice finds nothing the second time.
    /// </remarks>
    public static async Task<string> AlertAsync(HttpClient client, string kind)
    {
        ArgumentNullException.ThrowIfNull(client);

        Match match = Regex.Match(await GetAsync(client, "/Cameras"), $"""alert-{kind}" role="alert">([^<]*)<""");

        return match.Success ? WebUtility.HtmlDecode(match.Groups[1].Value) : string.Empty;
    }

    /// <summary>The application's own wording for <paramref name="key"/>, so a test does not pin English.</summary>
    public static string Localised(WebApplicationFactory<PrinterAppController> factory, string key)
    {
        ArgumentNullException.ThrowIfNull(factory);

        using IServiceScope scope = factory.Services.CreateScope();

        return scope.ServiceProvider.GetRequiredService<IStringLocalizer<SharedResource>>()[key].Value;
    }

    private static async Task<Guid> TeamUuidOfAsync(WebApplicationFactory<PrinterAppController> factory, HSUser user)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        HomespoolDbContext context = scope.ServiceProvider.GetRequiredService<HomespoolDbContext>();

        int teamId = await context.TeamMembers
                                  .Where(member => member.UserId == user.Id)
                                  .Select(member => member.TeamId)
                                  .FirstAsync(TestContext.Current.CancellationToken);

        return await context.Teams
                            .Where(team => team.Id == teamId)
                            .Select(team => team.Uuid)
                            .SingleAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<string> GetAsync(HttpClient client, string path)
    {
        using HttpResponseMessage response = await client.GetAsync(path, TestContext.Current.CancellationToken);

        return await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
    }
}
