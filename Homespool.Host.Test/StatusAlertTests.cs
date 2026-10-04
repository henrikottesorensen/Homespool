using AwesomeAssertions;

using Homespool.Host.Pages;

namespace Homespool.Host.Test;

/// <summary>
/// How a status message's kind is dressed: one contextual class per kind, and a warning for a
/// message nobody classified.
/// </summary>
public class StatusAlertTests
{
    [Theory]
    [InlineData(StatusKind.Success, "alert-success")]
    [InlineData(StatusKind.Info, "alert-info")]
    [InlineData(StatusKind.Warning, "alert-warning")]
    [InlineData(StatusKind.Danger, "alert-danger")]
    public void EachKindHasItsOwnClass(StatusKind kind, string expected)
    {
        new StatusAlert("Anything.", kind).AlertClass.Should().Be(expected);
    }

    /// <summary>
    /// A page that sets a message and forgets its kind shows a warning, never a success: the miss is
    /// visible, and a failure left unclassified does not read as good news.
    /// </summary>
    [Fact]
    public void AnUnclassifiedMessageIsAWarning()
    {
        new StatusAlert("Anything.", StatusKind.Undefined).AlertClass.Should().Be("alert-warning");
    }
}
