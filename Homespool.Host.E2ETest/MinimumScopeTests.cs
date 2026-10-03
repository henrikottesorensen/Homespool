using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

using Homespool.Host.Accounts;
using Homespool.Host.Authorisation;
using Homespool.Host.Controllers;
using Homespool.Model;

namespace Homespool.Host.E2ETest;

/// <summary>
/// Every route a personal access token can reach, called with exactly the least scope it should
/// need, and again with everything but each capability of that scope.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists: a test that never narrows the caller cannot see a branch that needs more than
/// the minimum.</b> A token minted with <see cref="CapabilitySet.Everything"/>, or a cookie session,
/// passes every gate, so a code path that quietly asks for a capability the route's real callers do
/// not hold is green in every test and refused in production.
/// </para>
/// <para>
/// <b>The minimum is stated, not discovered</b> - see <see cref="MinimumScopeRoute.Minimum"/>. The
/// two theories check the statement from both sides; <see cref="EveryTokenReachableActionHasARow"/>
/// makes a new action state one.
/// </para>
/// <para>
/// <b>Status codes only.</b> What a route does past the gate, and how a refusal words itself, are
/// each route's own tests' subject. These ask one thing: does the gate let exactly the minimum in.
/// </para>
/// </remarks>
public sealed class MinimumScopeTests : IAsyncLifetime
{
    private readonly ScratchDirectory _scratch = ScratchDirectory.Create("minimum-scope");
    private FakeGo2Rtc _sidecar = null!;
    private HomespoolFactory _factory = null!;
    private MinimumScopeWorld _world = null!;

    public static TheoryData<string> Routes => [.. MinimumScopeRoutes.All.Select(route => route.Name)];

    /// <summary>Each row's capabilities, one at a time.</summary>
    public static TheoryData<string, Capability> Removals
    {
        get
        {
            TheoryData<string, Capability> removals = [];

            foreach (MinimumScopeRoute route in MinimumScopeRoutes.All)
            {
                foreach (Capability removed in route.Minimum)
                {
                    removals.Add(route.Name, removed);
                }
            }

            return removals;
        }
    }

    public async ValueTask InitializeAsync()
    {
        _sidecar = await FakeGo2Rtc.StartAsync();

        _factory = new HomespoolFactory(_scratch);
        _sidecar.ApplyTo(_factory);
        _factory.ConfigurationOverrides["Cameras:WebRtcCandidate"] = "192.0.2.10:8555";

        _ = _factory.Server;

        using (IServiceScope scope = _factory.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<SetupState>().MarkComplete();
        }

        _world = new MinimumScopeWorld(_factory, _sidecar);
    }

    public async ValueTask DisposeAsync()
    {
        await _world.DisposeAsync();
        await _factory.DisposeAsync();
        await _sidecar.DisposeAsync();

        _scratch.Dispose();
    }

    /// <summary>The stated minimum gets the route past every capability check.</summary>
    [Theory]
    [MemberData(nameof(Routes))]
    public async Task TheMinimumScopeIsEnough(string name)
    {
        // Arrange
        MinimumScopeRoute route = MinimumScopeRoutes.Named(name);

        await route.Arrange(_world);

        using HttpClient client = await _world.ScopedClientAsync(route.Minimum, route.SlicerHeader);

        // Act
        using HttpRequestMessage request = route.Request(_world);
        using HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
                                                                    TestContext.Current.CancellationToken);

        // Assert
        response.StatusCode.Should().Be(route.Passes,
                                        $"a token scoped to [{string.Join(", ", route.Minimum)}] is all {name} should need");

        if (route.Refused == ScopeRefusal.EmptyList)
        {
            (await CountOfAsync(response)).Should().BePositive("the listing's refusal is an empty list, so the pass must not be one");
        }
    }

    /// <summary>
    /// Every capability in the stated minimum is needed: a token holding everything else is refused.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Everything else, not the minimum less one.</b> Gates only ever narrow, so a refusal of
    /// everything-but is a refusal of every smaller token too - and it is the only form that sees a
    /// gate <em>relaxed</em> rather than removed. The minimum less <see cref="Capability.ControlPrinter"/>
    /// is an empty token, refused by the printer lookup whatever the gate behind it asks for; everything
    /// but <see cref="Capability.ControlPrinter"/> holds <see cref="Capability.Print"/>, so a gate
    /// loosened to it lets the token through and the test fails.
    /// </para>
    /// <para>
    /// "Everything else" also leaves out whatever implies the removed capability, or storing the
    /// token would put it back, and the row's <see cref="MinimumScopeRoute.Alternatives"/>, which get
    /// through on their own.
    /// </para>
    /// </remarks>
    [Theory]
    [MemberData(nameof(Removals))]
    public async Task EachCapabilityOfTheMinimumIsNeeded(string name, Capability removed)
    {
        // Arrange
        MinimumScopeRoute route = MinimumScopeRoutes.Named(name);

        await route.Arrange(_world);

        using HttpClient client = await _world.ScopedClientAsync(EverythingBut(removed, route.Alternatives), route.SlicerHeader);

        // Act
        using HttpRequestMessage request = route.Request(_world);
        using HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
                                                                    TestContext.Current.CancellationToken);

        // Assert
        string because = $"{name} states it needs {removed}, and a gate that lets a token without it through is missing or loosened";

        switch (route.Refused)
        {
            case ScopeRefusal.Forbidden:
                response.StatusCode.Should().Be(HttpStatusCode.Forbidden, because);
                break;

            case ScopeRefusal.EmptyList:
                response.StatusCode.Should().Be(HttpStatusCode.OK, because);
                (await CountOfAsync(response)).Should().Be(0, because);
                break;

            default:
                throw new InvalidOperationException($"{name} refuses nothing, so it has no capability to remove.");
        }
    }

    /// <summary>
    /// Every action a token can reach has a row - so a new one cannot ship without somebody writing
    /// down the least scope it should need.
    /// </summary>
    /// <remarks>
    /// The controllers are found by their policy rather than listed, because a list is exactly what
    /// a new controller is left out of.
    /// </remarks>
    [Fact]
    public void EveryTokenReachableActionHasARow()
    {
        HashSet<string> covered = [.. MinimumScopeRoutes.All.Select(route => $"{route.Controller.Name}.{route.Action}")];

        IEnumerable<string> reachable = typeof(PrinterAppController).Assembly
                                                                    .GetTypes()
                                                                    .Where(IsTokenReachable)
                                                                    .SelectMany(controller => Actions(controller)
                                                                                    .Select(action => $"{controller.Name}.{action.Name}"));

        reachable.Should().NotBeEmpty("the controllers are found by their policy, and finding none means the search is broken")
                 .And.OnlyContain(action => covered.Contains(action),
                                  "every action under Policies.Api or Policies.Compat needs a row stating its least scope");
    }

    /// <summary>Every row names an action a token can reach, once per way through it.</summary>
    [Fact]
    public void EveryRowNamesATokenReachableAction()
    {
        MinimumScopeRoutes.All.Select(route => route.Name).Should().OnlyHaveUniqueItems();

        foreach (MinimumScopeRoute route in MinimumScopeRoutes.All)
        {
            IsTokenReachable(route.Controller).Should().BeTrue($"{route.Name} is under neither Policies.Api nor Policies.Compat");
            Actions(route.Controller).Select(action => action.Name).Should().Contain(route.Action,
                                                                                    $"{route.Name} names a method that is not an action");
        }
    }

    /// <summary>
    /// A row's minimum names nothing it already implies, states a refusal exactly when it names
    /// anything at all, and names no alternative that is part of it.
    /// </summary>
    [Fact]
    public void EveryMinimumIsStatedWithoutRedundancy()
    {
        foreach (MinimumScopeRoute route in MinimumScopeRoutes.All)
        {
            foreach (Capability capability in route.Minimum)
            {
                Closed(Without(route.Minimum, capability)).Should().NotContain(capability,
                    $"{route.Name} names {capability}, which the rest of its minimum already implies");
            }

            (route.Refused == ScopeRefusal.None).Should().Be(route.Minimum.Count == 0,
                                                            $"{route.Name} refuses exactly when it needs something");
            route.Alternatives.Intersect(route.Minimum).Should().BeEmpty($"{route.Name}'s alternatives are in place of its minimum");
        }
    }

    private static bool IsTokenReachable(Type type)
    {
        return typeof(ControllerBase).IsAssignableFrom(type) &&
               !type.IsAbstract &&
               type.GetCustomAttributes<AuthorizeAttribute>(inherit: true)
                   .Any(authorize => authorize.Policy is Policies.Api or Policies.Compat);
    }

    private static IEnumerable<MethodInfo> Actions(Type controller)
    {
        return controller.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                         .Where(method => !method.IsSpecialName &&
                                          method.GetCustomAttribute<NonActionAttribute>() is null);
    }

    private static Capability[] Without(IEnumerable<Capability> scope, Capability removed)
    {
        return [.. scope.Where(capability => capability != removed)];
    }

    /// <summary>
    /// Every capability that grants neither <paramref name="removed"/> nor any of
    /// <paramref name="alternatives"/>, once a token's scope is closed over implications.
    /// </summary>
    private static Capability[] EverythingBut(Capability removed, IEnumerable<Capability> alternatives)
    {
        HashSet<Capability> excluded = [removed, .. alternatives];

        return [.. CapabilitySet.Everything.Granted.Where(capability => !Closed([capability]).Overlaps(excluded))];
    }

    /// <summary>A scope as it is stored: closed over implications.</summary>
    private static IReadOnlySet<Capability> Closed(IEnumerable<Capability> scope)
    {
        return CapabilitySet.Parse(CapabilitySet.Format(scope)).Granted;
    }

    private static async Task<int> CountOfAsync(HttpResponseMessage response)
    {
        using JsonDocument payload =
            JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        return payload.RootElement.GetArrayLength();
    }
}
