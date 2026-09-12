using System;
using System.Collections.Generic;
using System.Reflection;
using System.Security.Claims;
using System.Threading.Tasks;

using AwesomeAssertions;

using Duende.IdentityModel;

using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.RazorPages.Infrastructure;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Time.Testing;

using Homespool.Host.Authentication;

namespace Homespool.Host.Test;

/// <summary>
/// The recent proof: what it grants, when it lapses, the things it refuses, and what the filter behind
/// <see cref="RequireRecentProofAttribute"/> does with it.
/// </summary>
/// <remarks>
/// <b>The refusals are the file's reason for existing.</b> A proof that said yes to everything would
/// pass any test asserting that a granted one works, so a cookie belonging to another account, one
/// past its window, and one somebody edited each have their own test. The filter's half pins the two
/// things a reader of the attribute is promised: a handler-level declaration gates that handler alone,
/// and a refused request is sent to the proof page with its path and never replayed.
/// </remarks>
public sealed class RecentProofTests
{
    private const long Account = 7;

    private static readonly DateTimeOffset Now = new(2026, 9, 12, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void AGrantedProofIsLiveOnTheNextRequest()
    {
        Rig rig = new();
        DefaultHttpContext granting = Rig.Request();

        rig.Proof.Grant(granting, Account, "pwd");

        rig.Proof.IsProved(rig.Next(granting), Account).Should().BeTrue();
    }

    [Fact]
    public void NoCookieIsNoProof()
    {
        Rig rig = new();

        rig.Proof.IsProved(Rig.Request(), Account).Should().BeFalse();
    }

    /// <summary>
    /// One browser, two accounts: a proof earned by one is not a proof for the other. The window is
    /// short, but a shared machine is exactly where it would otherwise be spent.
    /// </summary>
    [Fact]
    public void AProofBelongsToTheAccountThatEarnedIt()
    {
        Rig rig = new();
        DefaultHttpContext granting = Rig.Request();
        rig.Proof.Grant(granting, Account, "pwd");

        rig.Proof.IsProved(rig.Next(granting), Account + 1).Should().BeFalse();
    }

    [Fact]
    public void ItLapsesOnceTheWindowHasPassed()
    {
        Rig rig = new();
        DefaultHttpContext granting = Rig.Request();
        rig.Proof.Grant(granting, Account, "pwd");

        rig.Time.Advance(RecentProof.Window + TimeSpan.FromSeconds(1));

        rig.Proof.IsProved(rig.Next(granting), Account).Should().BeFalse();
    }

    /// <summary>
    /// It slides: work inside the window keeps it, so a long session of administration is not
    /// interrupted, while ten minutes of nothing ends it.
    /// </summary>
    [Fact]
    public void ActivityInsideTheWindowCarriesItForward()
    {
        Rig rig = new();
        DefaultHttpContext granting = Rig.Request();
        rig.Proof.Grant(granting, Account, "pwd");

        rig.Time.Advance(RecentProof.Window - TimeSpan.FromMinutes(1));
        DefaultHttpContext first = rig.Next(granting);
        bool live = rig.Proof.IsProved(first, Account);

        rig.Time.Advance(RecentProof.Window - TimeSpan.FromMinutes(1));
        bool stillLive = rig.Proof.IsProved(rig.Next(first), Account);

        live.Should().BeTrue();
        stillLive.Should().BeTrue("each request reissues the cookie, so the window runs from the last one");
    }

    /// <summary>A page may ask for a shorter window than the shared one, and the shorter one is what it gets.</summary>
    [Fact]
    public void AShorterWindowIsHonoured()
    {
        Rig rig = new();
        DefaultHttpContext granting = Rig.Request();
        rig.Proof.Grant(granting, Account, "pwd");
        rig.Time.Advance(TimeSpan.FromMinutes(5));

        rig.Proof.IsProved(rig.Next(granting), Account, TimeSpan.FromMinutes(2)).Should().BeFalse();
        rig.Proof.IsProved(rig.Next(granting), Account).Should().BeTrue();
    }

    [Fact]
    public void AnEditedCookieIsNoProof()
    {
        Rig rig = new();
        DefaultHttpContext granting = Rig.Request();
        rig.Proof.Grant(granting, Account, "pwd");

        string cookie = rig.Next(granting).Request.Headers.Cookie.ToString();
        DefaultHttpContext tampered = Rig.Request();
        tampered.Request.Headers.Cookie = cookie[..^2] + "AA";

        rig.Proof.IsProved(tampered, Account).Should().BeFalse();
    }

    [Fact]
    public void ClearingItEndsIt()
    {
        Rig rig = new();
        DefaultHttpContext granting = Rig.Request();
        rig.Proof.Grant(granting, Account, "pwd");
        DefaultHttpContext next = rig.Next(granting);

        rig.Proof.Clear(next);

        next.Response.Headers.SetCookie.ToString().Should().Contain("Homespool.RecentProof=;");
    }

    /// <summary>Site-wide, since the pages that want it are everywhere; hidden from script; never sent cross-site.</summary>
    [Fact]
    public void TheCookieIsSiteWideHiddenFromScriptAndStrict()
    {
        Rig rig = new();
        DefaultHttpContext granting = Rig.Request();

        rig.Proof.Grant(granting, Account, "passkey");

        string setCookie = granting.Response.Headers.SetCookie.ToString();
        setCookie.Should().Contain("path=/;").And.Contain("httponly").And.Contain("samesite=strict");
    }

    // ---------- the filter ----------
    [Fact]
    public async Task AProvedRequestReachesADeclaredPage()
    {
        Rig rig = new();
        (RecentProofPageFilter filter, PageHandlerExecutingContext context, Next next) = rig.Filter(new GatedPage(), nameof(GatedPage.OnGet), proved: true);

        await filter.OnPageHandlerExecutionAsync(context, next.Invoke);

        next.Called.Should().BeTrue();
        context.Result.Should().BeNull();
    }

    [Fact]
    public async Task AnUnprovedRequestIsSentToProveWithThePathToComeBackTo()
    {
        Rig rig = new();
        (RecentProofPageFilter filter, PageHandlerExecutingContext context, Next next) =
            rig.Filter(new GatedPage(), nameof(GatedPage.OnGet), proved: false, path: "/Admin/Users/Detail/3");

        await filter.OnPageHandlerExecutionAsync(context, next.Invoke);

        next.Called.Should().BeFalse("the handler must not run for a request that has not proved itself");
        RedirectToPageResult redirect = context.Result.Should().BeOfType<RedirectToPageResult>().Which;
        redirect.PageName.Should().Be("/Account/Reauthenticate");
        redirect.RouteValues.Should().ContainKey("returnUrl").WhoseValue.Should().Be("/Admin/Users/Detail/3");
    }

    /// <summary>
    /// A POST refused here is not carried through the proof: the return address is the page's path
    /// with no handler and no body, so the act has to be asked for again deliberately.
    /// </summary>
    [Fact]
    public async Task ARefusedPostGoesBackToThePageAndNotIntoTheAct()
    {
        Rig rig = new();
        (RecentProofPageFilter filter, PageHandlerExecutingContext context, Next next) =
            rig.Filter(new GatedPage(), nameof(GatedPage.OnPostRemove), proved: false, path: "/Printers/Detail/5", method: HttpMethods.Post);

        await filter.OnPageHandlerExecutionAsync(context, next.Invoke);

        next.Called.Should().BeFalse();
        context.Result.Should().BeOfType<RedirectToPageResult>()
               .Which.RouteValues!["returnUrl"].Should().Be("/Printers/Detail/5", "the path alone, never the handler");
    }

    /// <summary>A proof earned by another account on this browser does not open the page for this one.</summary>
    [Fact]
    public async Task AnotherAccountsProofDoesNotOpenThePage()
    {
        Rig rig = new();
        (RecentProofPageFilter filter, PageHandlerExecutingContext context, Next next) =
            rig.Filter(new GatedPage(), nameof(GatedPage.OnGet), proved: true, provedAs: Account + 1);

        await filter.OnPageHandlerExecutionAsync(context, next.Invoke);

        next.Called.Should().BeFalse();
    }

    [Fact]
    public async Task AHandlerLevelDeclarationGatesThatHandlerAlone()
    {
        Rig rig = new();
        (RecentProofPageFilter filter, PageHandlerExecutingContext reading, Next nextReading) = rig.Filter(new MixedPage(), nameof(MixedPage.OnGet), proved: false);
        (_, PageHandlerExecutingContext acting, Next nextActing) = rig.Filter(new MixedPage(), nameof(MixedPage.OnPostRemove), proved: false);

        await filter.OnPageHandlerExecutionAsync(reading, nextReading.Invoke);
        await filter.OnPageHandlerExecutionAsync(acting, nextActing.Invoke);

        nextReading.Called.Should().BeTrue("the page's GET declares nothing");
        nextActing.Called.Should().BeFalse("the one handler that declares is the one that is gated");
    }

    [Fact]
    public async Task APageThatDeclaresNothingIsLeftAlone()
    {
        Rig rig = new();
        (RecentProofPageFilter filter, PageHandlerExecutingContext context, Next next) = rig.Filter(new PlainPage(), nameof(PlainPage.OnGet), proved: false);

        await filter.OnPageHandlerExecutionAsync(context, next.Invoke);

        next.Called.Should().BeTrue();
    }

    [Fact]
    public async Task AHandlerAskingForAShorterWindowGetsIt()
    {
        Rig rig = new();
        (RecentProofPageFilter filter, PageHandlerExecutingContext context, Next next) =
            rig.Filter(new MixedPage(), nameof(MixedPage.OnPostQuick), proved: true, provedAgo: TimeSpan.FromMinutes(5));

        await filter.OnPageHandlerExecutionAsync(context, next.Invoke);

        next.Called.Should().BeFalse("five minutes is inside the shared window and outside the handler's own");
    }

    private sealed class Rig
    {
        public Rig()
        {
            Time = new FakeTimeProvider(Now);
            Proof = new RecentProof(new EphemeralDataProtectionProvider(), Time);
        }

        public FakeTimeProvider Time { get; }

        public RecentProof Proof { get; }

        public static DefaultHttpContext Request()
        {
            return new DefaultHttpContext();
        }

        /// <summary>
        /// The next request from the same browser: whatever <paramref name="previous"/> told the
        /// browser to keep comes back as a cookie header.
        /// </summary>
        public DefaultHttpContext Next(DefaultHttpContext previous)
        {
            DefaultHttpContext next = Request();

            string setCookie = previous.Response.Headers.SetCookie.ToString();
            next.Request.Headers.Cookie = setCookie.Split(';')[0];

            return next;
        }

        /// <summary>
        /// The filter over a request for <paramref name="handler"/> of <paramref name="page"/>, signed in
        /// as <see cref="Account"/>, carrying a proof when <paramref name="proved"/> - earned by
        /// <paramref name="provedAs"/> (this account unless said otherwise), <paramref name="provedAgo"/>
        /// before now.
        /// </summary>
        public (RecentProofPageFilter filter, PageHandlerExecutingContext context, Next next) Filter(
            PageModel page,
            string handler,
            bool proved,
            long? provedAs = null,
            TimeSpan? provedAgo = null,
            string path = "/Admin/Settings",
            string method = "GET")
        {
            DefaultHttpContext http;

            if (proved)
            {
                // Granted now, then the clock moved on: a fake clock only runs forward.
                DefaultHttpContext granting = Request();
                Proof.Grant(granting, provedAs ?? Account, "pwd");
                Time.Advance(provedAgo ?? TimeSpan.Zero);
                http = Next(granting);
            }
            else
            {
                http = Request();
            }

            http.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(JwtClaimTypes.Subject, Account.ToString())], "test"));
            http.Request.Path = path;
            http.Request.Method = method;

            PageContext pageContext = new(new ActionContext(http, new RouteData(), new ActionDescriptor()));

            HandlerMethodDescriptor descriptor = new()
            {
                MethodInfo = page.GetType().GetMethod(handler, BindingFlags.Instance | BindingFlags.Public)!,
                Name = handler,
            };

            PageHandlerExecutingContext context = new(pageContext, [], descriptor, new Dictionary<string, object?>(), page);

            return (new RecentProofPageFilter(Proof), context, new Next(pageContext, context));
        }
    }

    private sealed class Next(PageContext pageContext, PageHandlerExecutingContext executing)
    {
        public bool Called { get; private set; }

        public Task<PageHandlerExecutedContext> Invoke()
        {
            Called = true;

            return Task.FromResult(new PageHandlerExecutedContext(pageContext, executing.Filters, executing.HandlerMethod, executing.HandlerInstance));
        }
    }

    [RequireRecentProof]
    private sealed class GatedPage : PageModel
    {
        public void OnGet()
        {
        }

        public void OnPostRemove()
        {
        }
    }

    private sealed class MixedPage : PageModel
    {
        public void OnGet()
        {
        }

        [RequireRecentProof]
        public void OnPostRemove()
        {
        }

        [RequireRecentProof(MaxAgeSeconds = 120)]
        public void OnPostQuick()
        {
        }
    }

    private sealed class PlainPage : PageModel
    {
        public void OnGet()
        {
        }
    }
}
