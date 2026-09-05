using AwesomeAssertions;

using Microsoft.Extensions.Options;

using Homespool.Host.PrusaConnect;

namespace Homespool.Host.Test;

/// <summary>
/// The printer host is refused at startup when a printer could not hold it.
/// </summary>
/// <remarks>
/// A 21-character name provisioned two printers with a name that does not exist, and nothing on this
/// side measured it. The limit is the firmware's field, recorded on the constant; these pin the edge
/// and the sentence.
/// </remarks>
public class PrinterHostLengthValidatorTests
{
    /// <summary>Twenty fits, twenty-one does not, and the message names what would be dialled.</summary>
    [Fact]
    public void TheLimitIsTwentyCharactersInclusive()
    {
        PrinterHostLengthValidator.Refusal("printers.example.net").Should().BeNull("20 characters is the field");
        PrinterHostLengthValidator.Refusal("homespool.example.net").Should()
                                  .Contain("21 characters").And.Contain("homespool.example.ne");
    }

    /// <summary>Whitespace around the value is not the printer's problem and does not count.</summary>
    [Fact]
    public void SurroundingWhitespaceDoesNotCount()
    {
        PrinterHostLengthValidator.Refusal("  printers.example.net  ").Should().BeNull();
    }

    /// <summary>An empty host is somebody else's refusal; this one only measures.</summary>
    [Fact]
    public void AnEmptyHostIsNotRefusedHere()
    {
        PrinterHostLengthValidator.Refusal(null).Should().BeNull();
        PrinterHostLengthValidator.Refusal(string.Empty).Should().BeNull();
    }

    /// <summary>Through the options interface, the failure names the setting an operator would edit.</summary>
    [Fact]
    public void TheOptionsValidationNamesTheSetting()
    {
        PrinterHostLengthValidator validator = new();

        ValidateOptionsResult refused = validator.Validate(null, new PrusaConnectOptions { PrinterHost = "homespool.example.net" });
        ValidateOptionsResult accepted = validator.Validate(null, new PrusaConnectOptions { PrinterHost = "printers.example.net" });

        refused.Failed.Should().BeTrue();
        refused.FailureMessage.Should().Contain("PRINTER_HOST").And.Contain("20-character");
        accepted.Succeeded.Should().BeTrue();
    }
}
