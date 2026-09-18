using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;

using AwesomeAssertions;

using Duende.IdentityModel;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Options;

using Homespool.Host.Authentication;

namespace Homespool.Host.Test;

/// <summary>
/// A provider's answer is made printable as it arrives: an identifier that cannot be is a refusal,
/// anything else is replaced and cut - and the ticket-received event is what does it.
/// </summary>
/// <remarks>
/// Every value here is one a provider writes, and several are the person's own to choose at the
/// provider. <c>PrintableTextTests</c> pins what printable means; this pins which claims are refused,
/// which are rewritten, and that the handler calls the rule at all. The escapes are written rather
/// than pasted, as there.
/// </remarks>
public class ExternalClaimsTests
{
    private const string Dirty = "\u001B[2J\u202E";

    // ---- identifiers are refused ----
    [Theory]
    [InlineData(JwtClaimTypes.Subject)]
    [InlineData(JwtClaimTypes.Email)]
    public void AnIdentifierHoldingAnUnprintableCharacterRefusesTheAnswer(string claimType)
    {
        ClaimsPrincipal answer = Answer(new Claim(JwtClaimTypes.Subject, "248289761001"),
                                        new Claim(JwtClaimTypes.Email, "anna@example.net"));
        Swap(answer, claimType, "anna" + Dirty + "@example.net");

        ExternalSignIn.TryMakePrintable(answer, out string? refused).Should().BeFalse();
        refused.Should().Be(claimType);
    }

    /// <summary>
    /// The rewrite that must never happen: two subjects differing only in a character nobody can see
    /// would both become the same provider key.
    /// </summary>
    [Fact]
    public void ARefusedIdentifierIsLeftExactlyAsItArrived()
    {
        ClaimsPrincipal answer = Answer(new Claim(JwtClaimTypes.Subject, "2482" + Dirty + "89761001"));

        ExternalSignIn.TryMakePrintable(answer, out _);

        answer.FindFirstValue(JwtClaimTypes.Subject).Should().Be("2482" + Dirty + "89761001");
    }

    [Theory]
    [InlineData(JwtClaimTypes.Subject, ExternalSignIn.MaxSubjectLength)]
    [InlineData(JwtClaimTypes.Email, ExternalSignIn.MaxEmailLength)]
    public void AnIdentifierIsRefusedPastItsLengthAndTakenAtIt(string claimType, int longest)
    {
        ExternalSignIn.TryMakePrintable(Answer(new Claim(claimType, new string('x', longest))), out _).Should().BeTrue();

        ExternalSignIn.TryMakePrintable(Answer(new Claim(claimType, new string('x', longest + 1))), out string? refused)
                      .Should().BeFalse();
        refused.Should().Be(claimType);
    }

    // ---- everything else is replaced and cut ----
    [Fact]
    public void ADisplayNameIsReplacedAndKeepsWhereItCameFrom()
    {
        Claim name = new(JwtClaimTypes.Name, "Anna" + Dirty, ClaimValueTypes.String, "https://provider.example", "https://upstream.example");
        name.Properties["shape"] = "kept";
        ClaimsPrincipal answer = Answer(new Claim(JwtClaimTypes.Subject, "248289761001"), name);

        ExternalSignIn.TryMakePrintable(answer, out _).Should().BeTrue();

        Claim cleaned = answer.FindAll(JwtClaimTypes.Name).Should().ContainSingle().Subject;
        cleaned.Value.Should().Be("Anna\uFFFD[2J\uFFFD");
        cleaned.Issuer.Should().Be("https://provider.example");
        cleaned.OriginalIssuer.Should().Be("https://upstream.example");
        cleaned.Properties.Should().Contain("shape", "kept");
        answer.Identity!.Name.Should().Be("Anna\uFFFD[2J\uFFFD", "the name a log line and a page read is this claim");
    }

    /// <summary>Cut with nothing added: this is somebody's name from here on, not a line in a log.</summary>
    [Fact]
    public void AnOverLongValueIsCutToTheBoundAndNothingIsAppended()
    {
        ClaimsPrincipal answer = Answer(new Claim(JwtClaimTypes.Name, new string('x', 5000)));

        ExternalSignIn.TryMakePrintable(answer, out _).Should().BeTrue();

        answer.FindFirstValue(JwtClaimTypes.Name).Should().Be(new string('x', ExternalSignIn.MaxClaimLength));
    }

    [Fact]
    public void AClaimWhoseTypeIsUnprintableOrOverLongIsDropped()
    {
        ClaimsPrincipal answer = Answer(new Claim(JwtClaimTypes.Subject, "248289761001"),
                                        new Claim("nick" + Dirty, "anna"),
                                        new Claim(new string('t', ExternalSignIn.MaxClaimLength + 1), "anna"));

        ExternalSignIn.TryMakePrintable(answer, out _).Should().BeTrue();

        answer.Claims.Should().ContainSingle().Which.Type.Should().Be(JwtClaimTypes.Subject);
    }

    [Fact]
    public void AnOrdinaryAnswerIsNotTouched()
    {
        Claim[] sent =
        [
            new(JwtClaimTypes.Subject, "248289761001"),
            new(JwtClaimTypes.Email, "anna.s\u00F8rensen@example.net"),
            new(JwtClaimTypes.EmailVerified, "true", ClaimValueTypes.Boolean),
            new(JwtClaimTypes.Name, "Anna S\u00F8rensen"),
        ];
        ClaimsPrincipal answer = Answer(sent);

        // The identity clones what it is given, so its own claims are the ones to recognise afterwards.
        List<Claim> arrived = answer.Claims.ToList();

        ExternalSignIn.TryMakePrintable(answer, out _).Should().BeTrue();

        answer.Claims.Should().Equal(arrived, (kept, original) => ReferenceEquals(kept, original));
    }

    // ---- and the handler is what calls it ----
    [Fact]
    public async Task TheTicketReceivedEventMakesTheAnswerPrintable()
    {
        (OpenIdConnectOptions options, TicketReceivedContext context, _) = TicketReceived(
            new Claim(JwtClaimTypes.Subject, "248289761001"),
            new Claim(JwtClaimTypes.Name, "Anna" + Dirty));

        await options.Events.TicketReceived(context);

        context.Result.Should().BeNull("an answer whose identifiers are clean goes on to be signed in");
        context.Principal!.FindFirstValue(JwtClaimTypes.Name).Should().Be("Anna\uFFFD[2J\uFFFD");
        context.Principal!.FindFirstValue(JwtClaimTypes.AuthenticationTime).Should().NotBeNull("the step after it still runs");
    }

    [Fact]
    public async Task TheTicketReceivedEventFailsAnAnswerWhoseSubjectIsNotPrintable()
    {
        (OpenIdConnectOptions options, TicketReceivedContext context, FakeLogCollector logs) = TicketReceived(
            new Claim(JwtClaimTypes.Subject, "2482" + Dirty + "89761001"));

        await options.Events.TicketReceived(context);

        context.Result!.Failure.Should().NotBeNull("the answer is never signed in to the external cookie");

        FakeLogRecord refusal = logs.GetSnapshot().Should().ContainSingle(record => record.Level == LogLevel.Warning).Subject;
        refusal.StructuredState.Should().Contain(pair => pair.Key == "ClaimType" && pair.Value == JwtClaimTypes.Subject);
        refusal.StructuredState.Should().Contain(pair => pair.Key == "LoginProvider" && pair.Value == Schemes.ExternalOidc);
        refusal.Message.Should().NotContain("2482", "the line names the claim, never its value");
    }

    private static ClaimsPrincipal Answer(params Claim[] claims)
    {
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "oidc", JwtClaimTypes.Name, JwtClaimTypes.Role));
    }

    private static void Swap(ClaimsPrincipal answer, string claimType, string value)
    {
        ClaimsIdentity identity = (ClaimsIdentity)answer.Identity!;

        identity.RemoveClaim(identity.FindFirst(claimType));
        identity.AddClaim(new Claim(claimType, value));
    }

    /// <summary>The scheme as <c>AddOidcAuthentication</c> registers it, and a ticket arriving on it.</summary>
    private static (OpenIdConnectOptions options, TicketReceivedContext context, FakeLogCollector logs) TicketReceived(params Claim[] claims)
    {
        IConfiguration configuration = new ConfigurationBuilder()
                                       .AddInMemoryCollection(new Dictionary<string, string?>
                                       {
                                           ["Oidc:Authority"] = "https://provider.example",
                                           ["Oidc:ClientId"] = "homespool",
                                           ["Oidc:ClientSecret"] = "not a secret", // betterleaks:allow - no provider is ever dialled
                                       })
                                       .Build();

        FakeLogCollector logs = new();
        ServiceCollection services = new();
        services.AddLogging(logging => logging.AddProvider(new FakeLoggerProvider(logs)));
        services.AddDataProtection();
        services.AddSingleton(TimeProvider.System);
        services.AddAuthentication().AddOidcAuthentication(configuration);

        ServiceProvider provider = services.BuildServiceProvider();
        OpenIdConnectOptions options = provider.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>().Get(Schemes.ExternalOidc);

        DefaultHttpContext request = new() { RequestServices = provider };
        AuthenticationScheme scheme = new(Schemes.ExternalOidc, "Single sign-on", typeof(OpenIdConnectHandler));
        AuthenticationTicket ticket = new(Answer(claims), new AuthenticationProperties(), Schemes.ExternalOidc);

        return (options, new TicketReceivedContext(request, scheme, options, ticket), logs);
    }
}
