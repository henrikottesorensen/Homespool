using AwesomeAssertions;

using Homespool.Host.Exceptions;
using Homespool.Model;

namespace Homespool.Host.Test;

/// <summary>
/// What a refused heater change tells the person who asked for it.
/// </summary>
/// <remarks>
/// The wording is the whole content of these: a refusal that misdescribes the reason sends someone
/// to the machine. Asserted rather than left to review, because a message is exactly the kind of
/// thing a later edit changes without noticing it carried a distinction.
/// </remarks>
public class PrinterBusyExceptionTests
{
    /// <summary>
    /// Not knowing is not being busy. A freshly connected printer is <c>Unknown</c> until its first
    /// telemetry merges, so this refusal is temporary and resolves without anyone doing anything.
    /// </summary>
    [Theory]
    [InlineData(PrinterStatus.Unknown)]
    [InlineData(PrinterStatus.Undefined)]
    public void AnUnknownStateSaysSoRatherThanBlamingThePrinter(PrinterStatus status)
    {
        string message = new PrinterBusyException(status).Message;

        message.Should().Contain("isn't known yet");
        message.Should().NotContain("not busy",
            "that sentence describes a printer doing something, which this one is not");
    }

    /// <summary>A state that really is mid-something names itself and says what is allowed.</summary>
    [Theory]
    [InlineData(PrinterStatus.Printing)]
    [InlineData(PrinterStatus.Paused)]
    [InlineData(PrinterStatus.Attention)]
    public void ABusyStateNamesItself(PrinterStatus status)
    {
        string message = new PrinterBusyException(status).Message;

        message.Should().Contain(status.ToString());
        message.Should().Contain("not busy", "the rule is stated, not the enum spelled out");
    }
}
