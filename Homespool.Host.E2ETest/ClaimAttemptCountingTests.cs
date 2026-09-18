using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Homespool.Data;
using Homespool.Host.Accounts;
using Homespool.Model;
using Homespool.Model.Entities;

namespace Homespool.Host.E2ETest;

/// <summary>
/// That a failed claim is actually counted against the account that made it, on the claim page and
/// on <c>POST /api/v1/printers/register</c> alike.
/// </summary>
/// <remarks>
/// <para>
/// <b>Driven through the real page rather than by modelling its shape</b>, and that is the point of
/// putting this in the E2E suite. The defect this guards was entirely in how <c>OnPostAsync</c>
/// scoped its transaction — a unit test that reproduced the scoping by hand would have passed
/// against the fix while proving nothing about the page.
/// </para>
/// <para>
/// <b>Why it matters:</b> the per-account cap is the only thing bounding registration-code guessing.
/// The global rate limiter deliberately cannot reach an authenticated page, and the counter lives on the
/// user rather than on the registration precisely because a wrong code finds no registration to
/// count against.
/// </para>
/// <para>
/// Found on hardware 2026-08-17: two genuinely mistyped claims on the Pi 3 appliance left
/// <c>FailedClaimAttempts</c> at 0.
/// </para>
/// </remarks>
public sealed class ClaimAttemptCountingTests : IAsyncLifetime
{
    private readonly ScratchDirectory _scratch = ScratchDirectory.Create("claimcount");
    private HomespoolFactory _factory = null!;

    public ValueTask InitializeAsync()
    {
        _factory = new HomespoolFactory(_scratch);

        _ = _factory.Server;

        using IServiceScope scope = _factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<SetupState>().MarkComplete();

        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task AClaimWithAnUnknownCodeIsCountedAgainstTheAccount()
    {
        (HSUser user, HttpClient client) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "claimer@example.com");

        using (client)
        {
            (await PostClaimAsync(client, "ZZZZZZZZZZ")).Should().Be(System.Net.HttpStatusCode.OK,
                "a refused claim redisplays the page; a 400 would mean it never reached the handler");

            (await AttemptsAsync(user.Id)).Should().Be(1,
                "the per-account cap is the only bound on code guessing, so a failed claim that " +
                "counts as zero leaves it unbounded");

            // A second one, because an off-by-one that only ever records the first attempt would
            // satisfy the assertion above and still leave the cap unreachable.
            _ = await PostClaimAsync(client, "YYYYYYYYYY");

            (await AttemptsAsync(user.Id)).Should().Be(2, "each attempt counts, not just the first");
        }
    }

    /// <summary>
    /// The other half of the rule: a code that works clears the count, and that reset must survive
    /// the transaction the claim was made in.
    /// </summary>
    [Fact]
    public async Task ASuccessfulClaimClearsTheCount()
    {
        (HSUser user, HttpClient client) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "resetter@example.com");

        using (client)
        {
            _ = await PostClaimAsync(client, "ZZZZZZZZZZ");
            (await AttemptsAsync(user.Id)).Should().Be(1);

            string code = await SeedRegistrationAsync("ABCDEFGHJK");

            (await PostClaimAsync(client, code)).Should().Be(System.Net.HttpStatusCode.Found,
                "a claim that worked redirects to the printer list");

            (await AttemptsAsync(user.Id)).Should().Be(0, "a correct code clears the account's count");
        }
    }

    /// <summary>
    /// The JSON claim is counted too - it was once the unbounded way round the page's cap.
    /// </summary>
    [Fact]
    public async Task AnApiClaimWithAnUnknownCodeIsCountedAgainstTheAccount()
    {
        (HSUser user, HttpClient client) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "apiclaimer@example.com");

        using (client)
        {
            (await PostApiClaimAsync(client, "ZZZZZZZZZZ")).Should().Be(System.Net.HttpStatusCode.NotFound);
            (await AttemptsAsync(user.Id)).Should().Be(1);

            _ = await PostApiClaimAsync(client, "YYYYYYYYYY");
            (await AttemptsAsync(user.Id)).Should().Be(2, "each attempt counts, not just the first");
        }
    }

    /// <summary>
    /// One allowance across both surfaces. Two separate counts would hand a guesser double the budget
    /// by alternating between them.
    /// </summary>
    [Fact]
    public async Task ThePageAndTheApiShareOneAllowance()
    {
        (HSUser user, HttpClient client) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "alternator@example.com");

        using (client)
        {
            _ = await PostClaimAsync(client, "ZZZZZZZZZZ");
            _ = await PostApiClaimAsync(client, "YYYYYYYYYY");

            (await AttemptsAsync(user.Id)).Should().Be(2);
        }
    }

    /// <summary>
    /// Guessing through the API reaches the backoff, and once there even the right code is refused
    /// unread - a lockout that still looked the code up would tell a guesser when they had hit.
    /// </summary>
    [Fact]
    public async Task ABackedOffAccountIsRefusedOnTheApiEvenWithTheRightCode()
    {
        (_, HttpClient client) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "guesser@example.com");

        using (client)
        {
            System.Net.HttpStatusCode last = System.Net.HttpStatusCode.NotFound;

            for (int i = 0; i < 20 && last == System.Net.HttpStatusCode.NotFound; i++)
            {
                last = await PostApiClaimAsync(client, "ZZZZZZZZZZ");
            }

            last.Should().Be(System.Net.HttpStatusCode.TooManyRequests, "enough wrong codes back the account off");

            string code = await SeedRegistrationAsync("ABCDEFGHJK");

            (await PostApiClaimAsync(client, code)).Should().Be(System.Net.HttpStatusCode.TooManyRequests);

            (await IsClaimedAsync(code)).Should().BeFalse("a refused claim must not have claimed anything");
        }
    }

    /// <summary>
    /// The API normalises what was typed, as the page does - otherwise a correct code in lowercase
    /// would be refused and, now that refusals count, cost an attempt as well.
    /// </summary>
    [Fact]
    public async Task AnApiClaimAcceptsACodeTypedInLowercaseWithAHyphen()
    {
        (HSUser user, HttpClient client) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "typist@example.com");

        using (client)
        {
            _ = await PostApiClaimAsync(client, "ZZZZZZZZZZ");

            string code = await SeedRegistrationAsync("ABCDEFGHJK");

            (await PostApiClaimAsync(client, "abcde-fghjk")).Should().Be(System.Net.HttpStatusCode.Created);

            (await AttemptsAsync(user.Id)).Should().Be(0, "a correct code clears the count on this surface too");
            (await IsClaimedAsync(code)).Should().BeTrue();
        }
    }

    /// <summary>
    /// Seeds a pending registration directly, rather than posting <c>/p/register</c>: that route is
    /// served only on the printer listener, so a client from this factory gets the 404 that
    /// segregation exists to give. The claim path does not care how the row arrived.
    /// </summary>
    private async Task<string> SeedRegistrationAsync(string code)
    {
        using IServiceScope scope = _factory.Services.CreateScope();

        HomespoolDbContext database = scope.ServiceProvider.GetRequiredService<HomespoolDbContext>();

        database.PrusaConnectRegistrations.Add(new PrusaConnectRegistration
        {
            SerialNumber = "TEST-0001",
            FingerPrint = "TESTFINGERPRINT0000000000000000000000000000000000",
            TemporaryCode = code,
            TemporaryCodeExpiry = DateTimeOffset.UtcNow.AddMinutes(30),
            CreatedAt = DateTimeOffset.UtcNow,
        });

        await database.SaveChangesAsync(TestContext.Current.CancellationToken);

        return code;
    }

    private async Task<System.Net.HttpStatusCode> PostClaimAsync(HttpClient client, string code)
    {
        string page = await client.GetStringAsync("/Printers/Claim", TestContext.Current.CancellationToken);

        using FormUrlEncodedContent body = new(new Dictionary<string, string>
        {
            ["Input.Code"] = code,
            ["__RequestVerificationToken"] = AntiforgeryTestHelper.ExtractToken(page),
        });

        using HttpResponseMessage response = await client.PostAsync("/Printers/Claim", body, TestContext.Current.CancellationToken);

        return response.StatusCode;
    }

    private static async Task<System.Net.HttpStatusCode> PostApiClaimAsync(HttpClient client, string code)
    {
        using HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/printers/register", new { code },
                                                                          TestContext.Current.CancellationToken);

        return response.StatusCode;
    }

    private async Task<bool> IsClaimedAsync(string code)
    {
        using IServiceScope scope = _factory.Services.CreateScope();

        HomespoolDbContext database = scope.ServiceProvider.GetRequiredService<HomespoolDbContext>();

        return await database.PrusaConnectRegistrations
                             .AsNoTracking()
                             .AnyAsync(r => r.TemporaryCode == code && r.PrinterId != null,
                                       TestContext.Current.CancellationToken);
    }

    private async Task<int> AttemptsAsync(long userId)
    {
        using IServiceScope scope = _factory.Services.CreateScope();

        HomespoolDbContext database = scope.ServiceProvider.GetRequiredService<HomespoolDbContext>();

        // The count lives in UserActionAttempts keyed on the action, not on HSUser - and a reset
        // deletes the row rather than zeroing it, so "no row" is a count of zero.
        UserActionAttempt? attempt = await database.UserActionAttempts
                                                   .AsNoTracking()
                                                   .SingleOrDefaultAsync(
                                                       a => a.UserId == userId &&
                                                            a.Action == LimitedAction.ClaimPrinter,
                                                       TestContext.Current.CancellationToken);

        return attempt?.FailedCount ?? 0;
    }

    public async ValueTask DisposeAsync()
    {
        await _factory.DisposeAsync();

        _scratch.Dispose();
    }
}
