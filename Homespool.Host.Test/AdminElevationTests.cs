using System;

using AwesomeAssertions;

using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Time.Testing;

using Homespool.Host.Authentication;

namespace Homespool.Host.Test;

/// <summary>
/// The administration elevation: what it grants, when it lapses, and the three things it refuses.
/// </summary>
/// <remarks>
/// <b>The refusals are the file's reason for existing.</b> An elevation that said yes to everything
/// would pass any test asserting that a granted one works, so a cookie belonging to another account,
/// one past its window, and one somebody edited each have their own test - and each was checked by
/// removing the branch and watching exactly that one go red.
/// </remarks>
public sealed class AdminElevationTests
{
    private const long Administrator = 7;

    [Fact]
    public void AGrantedElevationIsLiveOnTheNextRequest()
    {
        // Arrange
        Rig rig = new();
        DefaultHttpContext granting = Rig.Request();

        // Act
        rig.Elevation.Grant(granting, Administrator);

        // Assert
        rig.Elevation.IsElevated(rig.Next(granting), Administrator).Should().BeTrue();
    }

    [Fact]
    public void NoCookieIsNoElevation()
    {
        Rig rig = new();

        rig.Elevation.IsElevated(Rig.Request(), Administrator).Should().BeFalse();
    }

    /// <summary>
    /// One browser, two accounts: an elevation earned by one is not an elevation for the other. The
    /// window is short, but a shared machine is exactly where it would otherwise be spent.
    /// </summary>
    [Fact]
    public void AnElevationBelongsToTheAccountThatEarnedIt()
    {
        // Arrange
        Rig rig = new();
        DefaultHttpContext granting = Rig.Request();
        rig.Elevation.Grant(granting, Administrator);

        // Act, Assert
        rig.Elevation.IsElevated(rig.Next(granting), Administrator + 1).Should().BeFalse();
    }

    [Fact]
    public void ItLapsesOnceTheWindowHasPassed()
    {
        // Arrange
        Rig rig = new();
        DefaultHttpContext granting = Rig.Request();
        rig.Elevation.Grant(granting, Administrator);

        // Act
        rig.Time.Advance(AdminElevation.Window + TimeSpan.FromSeconds(1));

        // Assert
        rig.Elevation.IsElevated(rig.Next(granting), Administrator).Should().BeFalse();
    }

    /// <summary>
    /// It slides: work inside the window keeps it, so a long session of administration is not
    /// interrupted, while ten minutes of nothing ends it.
    /// </summary>
    [Fact]
    public void ActivityInsideTheWindowCarriesItForward()
    {
        // Arrange
        Rig rig = new();
        DefaultHttpContext granting = Rig.Request();
        rig.Elevation.Grant(granting, Administrator);

        // Act - two checks, each most of a window after the last
        rig.Time.Advance(AdminElevation.Window - TimeSpan.FromMinutes(1));
        DefaultHttpContext first = rig.Next(granting);
        bool live = rig.Elevation.IsElevated(first, Administrator);

        rig.Time.Advance(AdminElevation.Window - TimeSpan.FromMinutes(1));
        bool stillLive = rig.Elevation.IsElevated(rig.Next(first), Administrator);

        // Assert
        live.Should().BeTrue();
        stillLive.Should().BeTrue("each request reissues the cookie, so the window runs from the last one");
    }

    [Fact]
    public void AnEditedCookieIsNoElevation()
    {
        // Arrange
        Rig rig = new();
        DefaultHttpContext granting = Rig.Request();
        rig.Elevation.Grant(granting, Administrator);

        DefaultHttpContext next = rig.Next(granting);
        string cookie = next.Request.Headers.Cookie.ToString();

        DefaultHttpContext tampered = Rig.Request();
        tampered.Request.Headers.Cookie = cookie[..^2] + "AA";

        // Act, Assert
        rig.Elevation.IsElevated(tampered, Administrator).Should().BeFalse();
    }

    [Fact]
    public void ClearingItEndsIt()
    {
        // Arrange
        Rig rig = new();
        DefaultHttpContext granting = Rig.Request();
        rig.Elevation.Grant(granting, Administrator);
        DefaultHttpContext next = rig.Next(granting);

        // Act
        rig.Elevation.Clear(next);

        // Assert - the response instructs the browser to drop it
        next.Response.Headers.SetCookie.ToString().Should().Contain("Homespool.AdminElevation=;");
    }

    /// <summary>The cookie goes nowhere but the administration screens, and no script may read it.</summary>
    [Fact]
    public void TheCookieIsScopedToTheAdministrationPathAndHiddenFromScript()
    {
        // Arrange
        Rig rig = new();
        DefaultHttpContext granting = Rig.Request();

        // Act
        rig.Elevation.Grant(granting, Administrator);

        // Assert
        string setCookie = granting.Response.Headers.SetCookie.ToString();
        setCookie.Should().Contain("path=/Admin").And.Contain("httponly").And.Contain("samesite=strict");
    }

    private sealed class Rig
    {
        public Rig()
        {
            Time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 8, 12, 0, 0, TimeSpan.Zero));
            Elevation = new AdminElevation(new EphemeralDataProtectionProvider(), Time);
        }

        public FakeTimeProvider Time { get; }

        public AdminElevation Elevation { get; }

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
            string value = setCookie.Split(';')[0];

            next.Request.Headers.Cookie = value;

            return next;
        }
    }
}
