using AwesomeAssertions;

using PrinterService.Api.PrusaConnect;

namespace PrinterService.Api.Test;

public class CodeGeneratorTests
{
    /// <summary>
    /// The firmware copies the <c>Code</c> response header into a fixed buffer and silently drops
    /// anything past it: <c>registrator.hpp</c> declares <c>CODE_SIZE = 25</c> and <c>ExtractCode</c>
    /// guards with <c>code_len &lt; CODE_SIZE</c>.
    /// </summary>
    private const int FirmwareCodeSizeLimit = 25;

    private readonly CodeGenerator _generator = new();

    /// <summary>
    /// The constraint with no other enforcement anywhere in the codebase.
    /// </summary>
    /// <remarks>
    /// Exceeding it does not fail loudly. The firmware truncates the code, then polls forever with a
    /// value the server never issued, so registration hangs rather than errors. Nothing in
    /// <see cref="CodeGenerator"/> references the firmware limit, so only this test stands between a
    /// one-character edit and every enrollment breaking.
    /// </remarks>
    [Fact]
    public void GeneratedCodeFitsInsideTheFirmwareBuffer()
    {
        for (int i = 0; i < 50; i++)
        {
            _generator.GenerateCode($"15715-{i}").Length.Should()
                      .BeLessThanOrEqualTo(FirmwareCodeSizeLimit,
                                           "registrator.hpp truncates past CODE_SIZE and the printer "
                                           + "then polls with a code the server never issued");
        }
    }

    /// <summary>
    /// The code contains only characters that survive an HTTP header and a human retyping them.
    /// </summary>
    /// <remarks>
    /// It travels in a <c>Code</c> response header and is then read off the printer's screen by a
    /// person claiming it, so anything needing escaping or easily mistyped would be a poor choice.
    /// Prusa's own codes in enrol.cap use the same alphabet.
    /// </remarks>
    [Fact]
    public void GeneratedCodeUsesOnlyUppercaseBase36()
    {
        // The capture shows Prusa's own codes in this alphabet, and it keeps the code safe to carry
        // in an HTTP header and read aloud off a printer screen.
        _generator.GenerateCode("15715-4842441651816441")
                  .Should().MatchRegex("^[0-9A-Z]+$");
    }

    /// <summary>
    /// Two codes for the same printer differ, because a nonce is mixed into the hash.
    /// </summary>
    /// <remarks>
    /// Without it the code would be a pure function of the serial number - which is printed on the
    /// machine and sent in the registration body - so anyone who knew the serial could compute the
    /// credential that claims the printer.
    /// </remarks>
    [Fact]
    public void SameSerialProducesADifferentCodeEachTime()
    {
        // A nonce is mixed in, so a code is not derivable from the serial number - which is printed
        // on the machine and travels in the registration body.
        string first = _generator.GenerateCode("15715-4842441651816441");
        string second = _generator.GenerateCode("15715-4842441651816441");

        second.Should().NotBe(first);
    }

    /// <summary>
    /// A smoke check that the code space is not trivially small.
    /// </summary>
    /// <remarks>
    /// <c>TemporaryCode</c> is deliberately non-uniquely indexed and <c>GetToken</c> looks it up with
    /// <c>SingleOrDefaultAsync</c>, so a collision surfaces as a 400 rather than as one printer
    /// receiving another's token. Not proof of uniqueness - a guard against a regression that
    /// shortened or weakened the code.
    /// </remarks>
    [Fact]
    public void CodesDoNotCollideAcrossManyGenerations()
    {
        // TemporaryCode is deliberately non-uniquely indexed, and GetToken uses SingleOrDefaultAsync,
        // so a collision surfaces as a 400 rather than a wrong lookup. This is a smoke check that the
        // space is not trivially small.
        HashSet<string> codes = [.. Enumerable.Range(0, 2_000).Select(i => _generator.GenerateCode($"sn-{i}"))];

        codes.Should().HaveCount(2_000);
    }
}
