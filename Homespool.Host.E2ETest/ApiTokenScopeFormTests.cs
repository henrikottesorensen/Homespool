using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.Extensions.DependencyInjection;

using Homespool.Host.Services;
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
/// of those leaves the page-model tests green - which is the mistake <c>notes/localisation.md</c>
/// records being made twice, by audits that could not see what they were looking for.
/// </remarks>
[Collection("WebApplicationFactory")]
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
    /// Every capability a token can carry has a checkbox, and every one of them is ticked when the
    /// form opens - so narrowing is deliberate and the default is the credential somebody expects.
    /// </summary>
    [Fact]
    public async Task TheFormOffersEveryCapabilityAndOpensWithAllOfThemTicked()
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
}
