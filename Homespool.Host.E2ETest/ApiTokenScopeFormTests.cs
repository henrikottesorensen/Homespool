using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.Extensions.DependencyInjection;

using Homespool.Host.Accounts;
using Homespool.Model;
using Homespool.Model.Entities;

namespace Homespool.Host.E2ETest;

/// <summary>
/// The token form's scope picker, rendered by a real host.
/// </summary>
/// <remarks>
/// <b>Rendered rather than asserted from the page model</b>, because the failures this guards are all
/// between the model and the browser: a capability with no label, a key that resolves to its own
/// name, a checkbox that carries the wrong value, or a service that was never registered. Every one
/// of those leaves the page-model tests green - a mistake made twice here already, by audits that
/// could not see what they were looking for.
/// </remarks>
public sealed class ApiTokenScopeFormTests : IAsyncLifetime, IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"hs-scopeform-{Guid.NewGuid():N}.db");

    private HomespoolFactory _factory = null!;

    public ValueTask InitializeAsync()
    {
        _factory = new HomespoolFactory($"Data Source={_databasePath}");

        _ = _factory.Server;

        // Until an administrator exists every navigable page funnels to /setup, so without this the
        // page under test answers 302 and an assertion about its content passes on an empty body.
        using IServiceScope scope = _factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<SetupState>().MarkComplete();

        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _factory.Dispose();

        return ValueTask.CompletedTask;
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
    /// Every capability a token can carry has a checkbox, and <b>not one of them is ticked when the
    /// form opens</b> - so every capability a token ends up with is one somebody chose.
    /// </summary>
    [Fact]
    public async Task TheFormOffersEveryCapabilityAndOpensWithNoneOfThemTicked()
    {
        // Arrange
        (HSUser _, HttpClient client) =
            await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(_factory, "scoped@example.com");

        using (client)
        {
            // Act
            using HttpResponseMessage response =
                await client.GetAsync("/Account/Manage/ApiTokens", TestContext.Current.CancellationToken);

            string page = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

            // Assert
            response.StatusCode.Should().Be(HttpStatusCode.OK,
                                             "redirected to {0}", response.Headers.Location?.ToString() ?? "(none)");

            foreach (Capability capability in CapabilitySet.Everything)
            {
                page.Should().Contain($"id=\"scope-{capability}\"", $"{capability} needs a box to tick");
                page.Should().Contain($"value=\"{capability}\"");
            }

            // Bootstrap renders a ticked box as a bare checked attribute; one per capability.
            page.Split("form-check-input").Length.Should()
                .Be(CapabilitySet.Everything.Count + 1, "one checkbox per capability and no more");

            page.Should().NotContain("checked", "a new token starts powerless, not maximal");
        }
    }

    /// <summary>
    /// <b>Every capability is named in the reader's language.</b> A missing key renders as the key
    /// itself, which is the failure this catches and which no page-model test can see.
    /// </summary>
    [Fact]
    public async Task EveryCapabilityIsLabelledRatherThanNamedByItsKey()
    {
        // Arrange
        (HSUser _, HttpClient client) =
            await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(_factory, "labelled@example.com");

        using (client)
        {
            // Act
            using HttpResponseMessage response =
                await client.GetAsync("/Account/Manage/ApiTokens", TestContext.Current.CancellationToken);

            string page = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

            // Assert
            response.StatusCode.Should().Be(HttpStatusCode.OK,
                                             "a redirect body contains no keys, so this would pass without rendering");

            List<string> unlabelled = CapabilitySet.Everything
                                                   .Where(capability => page.Contains($"Capability_{capability}",
                                                                                      StringComparison.Ordinal))
                                                   .Select(capability => capability.ToString())
                                                   .ToList();

            unlabelled.Should().BeEmpty("a resource key on the page means the label is missing");
        }
    }

    /// <summary>
    /// <b>Both buttons work with scripting off.</b> They are real submits with handlers behind them,
    /// and this drives exactly the path a browser that never ran <c>token-scope.js</c> takes - out to
    /// every box ticked and back to none.
    /// </summary>
    /// <remarks>
    /// The half a page-model test cannot see is that the boxes <i>render</i> in the state the handler
    /// chose. Setting <c>Input.Scope</c> and the view reading it are two separate things to be right
    /// about, and only the rendered page proves both.
    /// </remarks>
    [Fact]
    public async Task BothButtonsSetEveryBoxWithoutScripting()
    {
        // Arrange
        (HSUser _, HttpClient client) =
            await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(_factory, "ticker@example.com");

        using (client)
        {
            string opened = await client.GetStringAsync("/Account/Manage/ApiTokens",
                                                        TestContext.Current.CancellationToken);

            // Act - out. Nothing is ticked yet, so the form carries the half-typed name and no scope.
            using FormUrlEncodedContent tick = new(
            [
                new("__RequestVerificationToken", AntiforgeryTestHelper.ExtractToken(opened)),
                new KeyValuePair<string, string>("Input.Name", "half-typed"),
            ]);

            using HttpResponseMessage ticked = await client.PostAsync(
                "/Account/Manage/ApiTokens?handler=TickAll", tick, TestContext.Current.CancellationToken);

            string tickedPage = await ticked.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

            // Assert - out
            ticked.StatusCode.Should().Be(HttpStatusCode.OK,
                                          "redirected to {0}", ticked.Headers.Location?.ToString() ?? "(none)");

            // Razor renders a bool attribute as checked="checked" when true and omits it entirely when
            // false, so the pair is what to count - "checked" alone appears twice per ticked box.
            tickedPage.Split("checked=\"checked\"").Length.Should()
                      .Be(CapabilitySet.Everything.Count + 1, "every box, and only the boxes");
            tickedPage.Should().Contain("half-typed", "a round trip must not cost what was typed");

            // Act - back, with everything the ticked page would now submit.
            using FormUrlEncodedContent untick = new(
            [
                new("__RequestVerificationToken", AntiforgeryTestHelper.ExtractToken(tickedPage)),
                new KeyValuePair<string, string>("Input.Name", "half-typed"),
                .. CapabilitySet.Everything.Select(
                    capability => new KeyValuePair<string, string>("Input.Scope", capability.ToString())),
            ]);

            using HttpResponseMessage cleared = await client.PostAsync(
                "/Account/Manage/ApiTokens?handler=UntickAll", untick, TestContext.Current.CancellationToken);

            string clearedPage = await cleared.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

            // Assert - back
            cleared.StatusCode.Should().Be(HttpStatusCode.OK);
            clearedPage.Should().Contain($"id=\"scope-{Capability.Print}\"", "the boxes are still on the page");
            clearedPage.Should().NotContain("checked", "every box is clear again");

            // Neither button minted anything, and neither complained about the unfinished form.
            using IServiceScope scope = _factory.Services.CreateScope();
            scope.ServiceProvider.GetRequiredService<Homespool.Data.HomespoolDbContext>()
                 .ApiTokens.Should().BeEmpty("setting boxes is not minting");
        }
    }

    /// <summary>
    /// <b>A token with no capabilities is refused by the real form.</b> Now that the picker opens
    /// empty, submitting an empty scope is what happens if it is never noticed - so this is the check
    /// standing between a name typed in a hurry and a credential that silently does nothing.
    /// </summary>
    /// <remarks>
    /// Driven over HTTP rather than from the page model, because the page-model test supplies the
    /// <c>ModelState</c> error itself and so cannot see whether <c>[MinLength(1)]</c> is on the
    /// property at all. This one posts a real form and lets binding decide.
    /// </remarks>
    [Fact]
    public async Task AFormWithNothingTickedMintsNothingAndSaysWhy()
    {
        // Arrange
        (HSUser _, HttpClient client) =
            await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(_factory, "scopeless@example.com");

        using (client)
        {
            string opened = await client.GetStringAsync("/Account/Manage/ApiTokens",
                                                        TestContext.Current.CancellationToken);

            // A complete name and not one capability - the form as somebody who missed the picker
            // entirely would submit it.
            using FormUrlEncodedContent form = new(
            [
                new("__RequestVerificationToken", AntiforgeryTestHelper.ExtractToken(opened)),
                new KeyValuePair<string, string>("Input.Name", "in-a-hurry"),
            ]);

            // Act
            using HttpResponseMessage response =
                await client.PostAsync("/Account/Manage/ApiTokens", form, TestContext.Current.CancellationToken);

            string page = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

            // Assert
            response.StatusCode.Should().Be(HttpStatusCode.OK, "the form comes back rather than redirecting");

            using IServiceScope scope = _factory.Services.CreateScope();
            scope.ServiceProvider.GetRequiredService<Homespool.Data.HomespoolDbContext>()
                 .ApiTokens.Should().BeEmpty("a scopeless token is refused");

            page.Should().NotContain(ApiTokenService.Prefix, "and no secret is shown");

            // The refusal has to say what to do about it, in the reader's language rather than as a
            // resource key - the failure `EveryCapabilityIsLabelledRatherThanNamedByItsKey` guards.
            page.Should().Contain("Choose at least one thing this token may do.");
            page.Should().NotContain("Tokens_ScopeRequired");
        }
    }
}
