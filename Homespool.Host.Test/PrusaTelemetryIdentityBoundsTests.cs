using System.ComponentModel.DataAnnotations;
using System.Reflection;

using AwesomeAssertions;

using Homespool.Host.PrusaConnect;
using Homespool.Host.PrusaConnect.DTO;
using Homespool.Host.PrusaConnect.DTO.EventMessages;
using Homespool.Host.Telemetry;

namespace Homespool.Host.Test;

/// <summary>
/// The length bounds an <c>INFO</c>'s identity strings are held to, and what happens to one that
/// exceeds them.
/// </summary>
/// <remarks>
/// <para>
/// <b>Registration and <c>INFO</c> carry the same three fields about the same printer</b>, so bounding
/// one and not the other achieves nothing: a value refused at registration can be stated a second
/// later over <c>INFO</c> and stored on a column that is SQLite <c>TEXT</c> and rewritten on every
/// subsequent one. <see cref="PrusaConnectConstants"/> holds the numbers for both, and the last test
/// here is what keeps the registration half pointing at them.
/// </para>
/// <para>
/// <b>What these bounds are not.</b> They do not make the stored string safe to render - the views
/// encode it, and the payload that made that necessary was 25 characters, well inside every limit
/// here. This is about an unbounded column being rewritten on every <c>INFO</c>, and nothing else.
/// </para>
/// </remarks>
public sealed class PrusaTelemetryIdentityBoundsTests
{
    /// <summary>
    /// Each field is checked at its own boundary rather than somewhere safely past it, and each in
    /// its own test - two of the three limits are the same number, so a theory keyed on the limit
    /// would silently exercise one field twice and the third never.
    /// </summary>
    [Fact]
    public void FirmwareIsTakenAtItsLimitAndRefusedOneCharacterPastIt()
    {
        Firmware(PrusaConnectConstants.FirmwareMaxLength).Should().NotBeNull("a value at the limit is within it");
        Firmware(PrusaConnectConstants.FirmwareMaxLength + 1).Should().BeNull("one character past it is not");

        static string? Firmware(int length)
        {
            return PrusaTelemetryMapping.ToIdentity(new InfoEventDataDTO { Firmware = new string('6', length) })
                                        .Firmware;
        }
    }

    /// <summary>The printer type, at its own boundary.</summary>
    [Fact]
    public void ThePrinterTypeIsTakenAtItsLimitAndRefusedOneCharacterPastIt()
    {
        Model(PrusaConnectConstants.PrinterTypeMaxLength).Should().NotBeNull("a value at the limit is within it");
        Model(PrusaConnectConstants.PrinterTypeMaxLength + 1).Should().BeNull("one character past it is not");

        static string? Model(int length)
        {
            return PrusaTelemetryMapping.ToIdentity(new InfoEventDataDTO { PrinterType = new string('M', length) })
                                        .Model;
        }
    }

    /// <summary>The serial number, at its own boundary.</summary>
    [Fact]
    public void TheSerialNumberIsTakenAtItsLimitAndRefusedOneCharacterPastIt()
    {
        Serial(PrusaConnectConstants.SerialNumberMaxLength).Should().NotBeNull("a value at the limit is within it");
        Serial(PrusaConnectConstants.SerialNumberMaxLength + 1).Should().BeNull("one character past it is not");

        static string? Serial(int length)
        {
            return PrusaTelemetryMapping.ToIdentity(new InfoEventDataDTO { SerialNumber = new string('S', length) })
                                        .SerialNumber;
        }
    }

    /// <summary>
    /// An over-length field costs that field and nothing else.
    /// </summary>
    /// <remarks>
    /// This is the behaviour the whole design turns on. Refusing the message would hand a printer a
    /// way to stop its own identity ever being recorded by padding one string, and truncating would
    /// store a version the printer never claimed to run.
    /// </remarks>
    [Fact]
    public void AnOverLongFirmwareDoesNotTakeTheRestOfTheInfoWithIt()
    {
        PrinterIdentityUpdate update = PrusaTelemetryMapping.ToIdentity(new InfoEventDataDTO
        {
            Firmware = new string('6', PrusaConnectConstants.FirmwareMaxLength + 1),
            PrinterType = "MK4",
            SerialNumber = "SN-12345",
            NozzleDiameter = 0.4f,
        });

        update.Firmware.Should().BeNull("the field that broke the bound is the field that is dropped");
        update.Model.Should().Be("MK4");
        update.SerialNumber.Should().Be("SN-12345");
        update.NozzleDiameter.Should().Be(0.4f);
    }

    /// <summary>
    /// A dropped field leaves what is stored alone rather than clearing it, because unreported and
    /// "reported as empty" are the same thing to the writer.
    /// </summary>
    /// <remarks>
    /// Null is how this wire says nothing arrived, so a rejected string cannot erase the last good
    /// one - a printer that states one bad version keeps the version it last stated properly.
    /// </remarks>
    [Fact]
    public void ARejectedStringIsIndistinguishableFromAnAbsentOne()
    {
        PrinterIdentityUpdate rejected = PrusaTelemetryMapping.ToIdentity(new InfoEventDataDTO
        {
            Firmware = new string('6', PrusaConnectConstants.FirmwareMaxLength + 1),
        });

        PrinterIdentityUpdate absent = PrusaTelemetryMapping.ToIdentity(new InfoEventDataDTO());

        rejected.Firmware.Should().Be(absent.Firmware);
    }

    /// <summary>
    /// Registration is held to the same numbers, read off the attributes that enforce it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Comparing the constants would be comparing a constant to itself.</b> What can still break is
    /// the <i>link</i> - somebody writing <c>[StringLength(64)]</c> back into the registration DTO,
    /// which compiles, passes every existing test, and silently unshares the number. So this reads the
    /// attribute that actually does the enforcing and asks whether it carries the shared value.
    /// </para>
    /// <para>
    /// It cannot see the difference between a literal <c>64</c> and the constant that equals 64 -
    /// nothing at runtime can, since the compiler folds both. What it does catch is the case that
    /// matters: a literal left behind while the constant moves.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData(nameof(RegisterPrinterRequestDTO.SerialNumber), PrusaConnectConstants.SerialNumberMaxLength)]
    [InlineData(nameof(RegisterPrinterRequestDTO.PrinterType), PrusaConnectConstants.PrinterTypeMaxLength)]
    [InlineData(nameof(RegisterPrinterRequestDTO.Firmware), PrusaConnectConstants.FirmwareMaxLength)]
    public void RegistrationEnforcesTheSameBound(string property, int expected)
    {
        PropertyInfo declared = typeof(RegisterPrinterRequestDTO).GetProperty(property)!;

        declared.Should().NotBeNull("the property is named with nameof, so this can only fail if it moved");

        StringLengthAttribute? bound = declared.GetCustomAttribute<StringLengthAttribute>();

        bound.Should().NotBeNull("the attribute is the only thing bounding this field - see the DTO's remarks");
        bound!.MaximumLength.Should().Be(expected, "registration and INFO hold one field to one number");
    }

    /// <summary>
    /// A real firmware string is nowhere near the bound - the check that the numbers are chosen
    /// rather than merely agreed.
    /// </summary>
    [Fact]
    public void TheVersionsPrintersActuallyStateAreWellInsideTheBound()
    {
        PrinterIdentityUpdate update = PrusaTelemetryMapping.ToIdentity(new InfoEventDataDTO
        {
            Firmware = "6.4.0+11974",
            PrinterType = "MK4",
            SerialNumber = "1234567890ABCDEFGHIJ",
        });

        update.Firmware.Should().Be("6.4.0+11974");
        update.Model.Should().Be("MK4");
        update.SerialNumber.Should().Be("1234567890ABCDEFGHIJ");
    }
}
