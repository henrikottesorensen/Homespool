using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.Extensions.DependencyInjection;

using Homespool.Host.Accounts;
using Homespool.Host.Configuration;
using Homespool.Model.Entities;

namespace Homespool.Host.E2ETest;

/// <summary>
/// The settings page over a deployment whose own configuration holds STUN on, beneath the settings
/// file - the case where what a post says and what the save leaves in force can differ.
/// </summary>
/// <remarks>
/// <b>End to end because the gap is the browser's.</b> An empty field reaches the page as a null, and
/// only the model binder decides that; the confirmation form is only exercised as rendered.
/// </remarks>
public sealed class SettingsLayerBeneathTests : IAsyncLifetime
{
    private const string Stun = "Cameras:WebRtcStunEnabled";

    private readonly ScratchDirectory _scratch = ScratchDirectory.Create("settings-beneath");
    private HomespoolFactory _factory = null!;

    public ValueTask InitializeAsync()
    {
        _factory = new HomespoolFactory(_scratch);
        _factory.ConfigurationOverrides[Stun] = "true";

        _ = _factory.Server;

        using IServiceScope scope = _factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<SetupState>().MarkComplete();

        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _factory.DisposeAsync();

        _scratch.Dispose();
    }

    /// <summary>
    /// Emptying the field removes STUN from the settings file, so the deployment's own "on" comes
    /// back - and that is turning it on, asked about like any other way of doing it.
    /// </summary>
    [Fact]
    public async Task AnEmptyFieldThatBringsStunBackIsAskedAbout()
    {
        using HttpClient admin = await AdminAsync("beneath-empty@example.com");

        string off = await PostAsync(admin, new Dictionary<string, string> { [Stun] = "false" });

        off.Should().Contain("Settings saved.");
        Current(Stun).Should().Be("false");

        string empty = await PostAsync(admin, new Dictionary<string, string> { [Stun] = string.Empty });

        empty.Should().Contain("name=\"Confirmed\"", "removing the saved value turns STUN back on");
        Current(Stun).Should().Be("false", "nothing is written while the question is open");
    }

    /// <summary>
    /// Mail is asked about first, STUN being on; another save turns STUN off before the answer, so
    /// the answer is asked about STUN. Answering that, as the page rendered it, saves both.
    /// </summary>
    [Fact]
    public async Task AnsweringAsRenderedKeepsEveryEarlierAnswer()
    {
        using HttpClient admin = await AdminAsync("beneath-answers@example.com");

        string first = await PostAsync(admin, new Dictionary<string, string>
        {
            [Stun] = "true",
            ["Smtp:Host"] = "mail.example.com",

            // Typed, because a new server does not take the password configured for the old one;
            // the question carries it sealed.
            ["Smtp:Password"] = "hunter2",
        });

        first.Should().Contain("name=\"Confirmed\" value=\"Smtp:Host\"");

        using (IServiceScope scope = _factory.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<SettingsStore>()
                 .Save(new Dictionary<string, string?> { [Stun] = "false" })
                 .Saved
                 .Should()
                 .BeTrue();
        }

        string second = await AnswerAsRenderedAsync(admin, first);

        second.Should().Contain($"name=\"Confirmed\" value=\"{Stun}\"");

        string third = await AnswerAsRenderedAsync(admin, second);

        third.Should().Contain("Settings saved.");
        Current(Stun).Should().Be("true");
        Current("Smtp:Host").Should().Be("mail.example.com");
    }

    private string Current(string path)
    {
        using IServiceScope scope = _factory.Services.CreateScope();

        return scope.ServiceProvider.GetRequiredService<SettingsStore>().Current()[path];
    }

    private async Task<HttpClient> AdminAsync(string email)
    {
        (HSUser _, HttpClient client) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, email, AdminBootstrap.AdminRole);

        await EnrolmentFlowHelper.ReauthenticateAsync(client);

        return client;
    }

    private static async Task<string> PostAsync(HttpClient client, Dictionary<string, string> values)
    {
        string page = await client.GetStringAsync("/Admin/Settings", TestContext.Current.CancellationToken);

        List<KeyValuePair<string, string>> form =
        [
            .. values.Select(pair => new KeyValuePair<string, string>($"Values[{pair.Key}]", pair.Value)),
            new("__RequestVerificationToken", AntiforgeryTestHelper.ExtractToken(page)),
        ];

        return await SubmitAsync(client, form);
    }

    /// <summary>
    /// Submits the confirmation form exactly as the page rendered it: every input inside it, and
    /// nothing else.
    /// </summary>
    private static async Task<string> AnswerAsRenderedAsync(HttpClient client, string page)
    {
        string question = Regex.Matches(page, "<form method=\"post\">.*?</form>", RegexOptions.Singleline)
                               .Select(match => match.Value)
                               .Single(form => form.Contains("name=\"Confirmed\"", StringComparison.Ordinal));

        List<KeyValuePair<string, string>> form =
        [
            .. Regex.Matches(question, "<input\\b[^>]*>")
                    .Select(input => new KeyValuePair<string, string>(
                        WebUtility.HtmlDecode(Regex.Match(input.Value, "name=\"([^\"]*)\"").Groups[1].Value),
                        WebUtility.HtmlDecode(Regex.Match(input.Value, "value=\"([^\"]*)\"").Groups[1].Value))),
        ];

        return await SubmitAsync(client, form);
    }

    private static async Task<string> SubmitAsync(HttpClient client, List<KeyValuePair<string, string>> form)
    {
        using FormUrlEncodedContent content = new(form);
        using HttpResponseMessage response = await client.PostAsync("/Admin/Settings", content, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        return await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
    }
}
