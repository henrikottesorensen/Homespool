using System;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Reflection;

using AwesomeAssertions;

using Microsoft.AspNetCore.Mvc;

using Homespool.Host.Controllers;
using Homespool.Host.Pages.Printers;
using Homespool.Host.PrusaConnect.DTO.App;
using Homespool.Model.Entities;

namespace Homespool.Host.Test;

/// <summary>
/// The length bounds on a printer's name and location, and the thing that keeps all four writers
/// holding the same one.
/// </summary>
/// <remarks>
/// <para>
/// <b>Four surfaces write these two columns</b> - the Add and Claim pages, and the app API's
/// register and patch bodies - so a bound held by some of them is no bound at all: a value one
/// refuses can be sent to another a second later and stored, on a column that is SQLite
/// <c>TEXT</c>. The attributes are the only thing enforcing this; nothing downstream re-checks, and
/// the provider ignores the length declared on the schema.
/// </para>
/// <para>
/// <b>Comparing the constants to themselves would prove nothing.</b> What can still break is the
/// <i>link</i> - somebody writing <c>[StringLength(200)]</c> back into one of the four, which
/// compiles, passes every other test, and silently unshares the number. So these read the attribute
/// that does the enforcing and ask whether it carries the shared value. Nothing at runtime can tell
/// a literal <c>200</c> from the constant that equals 200, since the compiler folds both; what this
/// catches is a literal left behind while the constant moves.
/// </para>
/// </remarks>
public sealed class PrinterNameBoundsTests
{
    /// <summary>
    /// Every surface that sets the name is held to <see cref="Printer.NameMaxLength"/>, and every
    /// one that sets the location to <see cref="Printer.LocationMaxLength"/>.
    /// </summary>
    /// <remarks>
    /// The two page models declare their fields on a nested <c>InputModel</c>, which is where the
    /// attribute has to be for model binding to see it - so that is what is read here rather than
    /// the page class.
    /// </remarks>
    [Theory]
    [InlineData(typeof(RegisterPrinterAppRequestDTO), nameof(RegisterPrinterAppRequestDTO.Name), Printer.NameMaxLength)]
    [InlineData(typeof(RegisterPrinterAppRequestDTO), nameof(RegisterPrinterAppRequestDTO.Location), Printer.LocationMaxLength)]
    [InlineData(typeof(PrinterPatchInputDTO), nameof(PrinterPatchInputDTO.Name), Printer.NameMaxLength)]
    [InlineData(typeof(PrinterPatchInputDTO), nameof(PrinterPatchInputDTO.Location), Printer.LocationMaxLength)]
    [InlineData(typeof(AddModel.InputModel), nameof(AddModel.InputModel.Name), Printer.NameMaxLength)]
    [InlineData(typeof(AddModel.InputModel), nameof(AddModel.InputModel.Location), Printer.LocationMaxLength)]
    [InlineData(typeof(ClaimModel.InputModel), nameof(ClaimModel.InputModel.Name), Printer.NameMaxLength)]
    [InlineData(typeof(ClaimModel.InputModel), nameof(ClaimModel.InputModel.Location), Printer.LocationMaxLength)]
    public void EverySurfaceThatWritesTheColumnCarriesTheSharedBound(Type surface, string property, int expected)
    {
        PropertyInfo? declared = surface.GetProperty(property);

        declared.Should().NotBeNull("the property is named with nameof, so this can only fail if it moved");

        StringLengthAttribute? bound = declared!.GetCustomAttribute<StringLengthAttribute>();

        bound.Should().NotBeNull($"{surface.Name}.{property} is bounded by this attribute and by nothing else");
        bound!.MaximumLength.Should().Be(expected, "all four writers of this column answer to one number");
    }

    /// <summary>
    /// The bound is a ceiling on a display string rather than a fit to one, so a realistic name is
    /// nowhere near it - the check that the number was chosen rather than merely agreed.
    /// </summary>
    [Fact]
    public void TheNamesPeopleActuallyChooseAreWellInsideTheBound()
    {
        "Living room MK4".Length.Should().BeLessThan(Printer.NameMaxLength / 4);
        "Workshop, upper shelf".Length.Should().BeLessThan(Printer.LocationMaxLength / 4);
    }

    /// <summary>
    /// Both app-API writes carry a request-size cap, which is the bound the length attributes cannot
    /// provide: those refuse a value only once the whole body has been buffered and deserialised.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Read rather than exercised, because the E2E suite cannot see this one.</b>
    /// <c>RequestSizeLimitAttribute</c> works by setting <c>IHttpMaxRequestBodySizeFeature</c>, and
    /// <c>TestServer</c> does not implement it - the same absence <c>BoundedUploadAttributeTests</c>
    /// records for <c>BoundedUploadAttribute</c>. A test driving the in-memory host reads a
    /// sixteen-kilobyte body past an eight-kilobyte cap and reaches the action, so it would assert
    /// the framework rather than the cap. Under Kestrel the ceiling is real and the refusal is a 413.
    /// </para>
    /// <para>
    /// So what can regress here is the attribute going missing or its number drifting, and that is
    /// what this reads.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData(nameof(PrinterAppController.RegisterPrinter))]
    [InlineData(nameof(PrinterAppController.PatchPrinter))]
    public void BothWritesCapTheRequestBody(string action)
    {
        MethodInfo? declared = typeof(PrinterAppController).GetMethod(action);

        declared.Should().NotBeNull("the method is named with nameof, so this can only fail if it moved");

        // The attribute keeps its limit in a private field, so the constructor argument as written is
        // the only readable copy of the number.
        CustomAttributeData? cap = declared!.GetCustomAttributesData()
                                            .SingleOrDefault(a => a.AttributeType == typeof(RequestSizeLimitAttribute));

        cap.Should().NotBeNull($"{action} accepts a body, and nothing else bounds how large it may be");
        cap!.ConstructorArguments.Single().Value.Should()
           .Be(8L * 1024, "a name, a location and a code are short, and the cap is generous against them");
    }
}
