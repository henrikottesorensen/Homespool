using System.Linq;
using System.Text;
using System.Text.Json;

using AwesomeAssertions;

using Homespool.Host.PrusaConnect;

namespace Homespool.Host.Test;

/// <summary>
/// Covers <see cref="NonFiniteNumberPatcher"/>: which spellings it mends, that it mends them only
/// where a value stands, and that everything else a parser refuses stays refused.
/// </summary>
/// <remarks>
/// The refusals matter as much as the mends. This runs on documents the parser has already turned
/// down, so whatever it lets through is a hole in the parser: every case below that must come back
/// null is a way of being almost right.
/// </remarks>
public class NonFiniteNumberPatcherTests
{
    /// <summary>The socket's reader options, comments included, so the comment case means something.</summary>
    private static readonly JsonReaderOptions Lenient = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
        AllowMultipleValues = true,
    };

    private static NonFinitePatch? Patch(string document, JsonReaderOptions options = default)
    {
        return NonFiniteNumberPatcher.TryPatch(Encoding.UTF8.GetBytes(document), options);
    }

    private static string Text(NonFinitePatch patch)
    {
        return Encoding.UTF8.GetString(patch.Document.Span);
    }

    /// <summary>
    /// Every spelling is mended as a property's value and as an array's element, and the same letters
    /// inside a string or as a property's name are not touched - nor is any other byte.
    /// </summary>
    /// <param name="spelling">The first four are what firmware's <c>"%.*f"</c> can print, the next
    /// three what Python's <c>json.dumps</c> writes; the rest are what another C library, <c>%F</c> or
    /// another client could.</param>
    /// <param name="literal">The quoted literal the serializer reads as that float. A NaN's sign
    /// carries nothing and the literal has none.</param>
    [Theory]
    [InlineData("nan", "NaN")]
    [InlineData("-nan", "NaN")]
    [InlineData("inf", "Infinity")]
    [InlineData("-inf", "-Infinity")]
    [InlineData("NaN", "NaN")]
    [InlineData("Infinity", "Infinity")]
    [InlineData("-Infinity", "-Infinity")]
    [InlineData("NAN", "NaN")]
    [InlineData("+nan", "NaN")]
    [InlineData("INF", "Infinity")]
    [InlineData("-INF", "-Infinity")]
    [InlineData("infinity", "Infinity")]
    [InlineData("-infinity", "-Infinity")]
    [InlineData("-INFINITY", "-Infinity")]
    [InlineData("Inf", "Infinity")]
    [InlineData("+inf", "Infinity")]
    [InlineData("+Infinity", "Infinity")]
    public void EverySpellingIsMendedWhereAValueStands(string spelling, string literal)
    {
        // Arrange
        string document = $$"""{"a":1,"v": {{spelling}} ,"arr":[{{spelling}},2],"s":"{{spelling}}","{{spelling}}":3}""";

        // Act
        NonFinitePatch? patch = Patch(document);

        // Assert
        patch.Should().NotBeNull();

        Text(patch).Should().Be($$"""{"a":1,"v": "{{literal}}" ,"arr":["{{literal}}",2],"s":"{{spelling}}","{{spelling}}":3}""");

        patch.Tokens.Should().Equal(new NonFiniteToken(0, spelling), new NonFiniteToken(1, spelling));

        using JsonDocument parsed = JsonDocument.Parse(patch.Document);

        parsed.RootElement.GetProperty("v").GetString().Should().Be(literal);
    }

    /// <summary>
    /// Almost a non-finite number is not one. Each of these is refused by the parser and has to stay
    /// refused, because whatever is let through here becomes a number the printer never sent.
    /// </summary>
    [Theory]
    [InlineData("infinit")]
    [InlineData("nanx")]
    [InlineData("nan(1)")]
    [InlineData("inf1")]
    [InlineData("in")]
    [InlineData("-")]
    [InlineData("+")]
    [InlineData("--inf")]
    [InlineData("+-inf")]
    [InlineData("nul")]
    [InlineData("-null")]
    [InlineData("1.#INF")]
    public void ANearMissStaysRefused(string value)
    {
        // Act
        NonFinitePatch? patch = Patch($$"""{"v":{{value}}}""");

        // Assert
        patch.Should().BeNull();
    }

    /// <summary>
    /// A right spelling in a wrong place stays refused: the reader is asked whether a value may stand
    /// there, and its answer is the only one taken.
    /// </summary>
    /// <param name="document">A spelling where a name belongs, after a name with no colon, after a
    /// value with no comma, after a comma in an object, and beside damage this does not mend.</param>
    [Theory]
    [InlineData("""{nan:1}""")]
    [InlineData("""{"a" nan}""")]
    [InlineData("""{"a":1 nan}""")]
    [InlineData("""{"a":1,nan}""")]
    [InlineData("""[1 inf]""")]
    [InlineData("""{"v":nan,,}""")]
    [InlineData("""{"v":nan""")]
    public void ASpellingWhereNoValueMayStandStaysRefused(string document)
    {
        // Act
        NonFinitePatch? patch = Patch(document);

        // Assert
        patch.Should().BeNull();
    }

    [Theory]
    [InlineData("""{"v":null,"w":1.5}""")]
    [InlineData("""{"v":"nan"}""")]
    [InlineData("")]
    public void ADocumentWithNothingToMendComesBackNull(string document)
    {
        // Act
        NonFinitePatch? patch = Patch(document);

        // Assert
        patch.Should().BeNull();
    }

    /// <summary>
    /// A replacement is known afterwards by which string value of the document it is, so the
    /// printer's own strings are counted with it - before, between and after, one of them spelled
    /// exactly like a replacement - and property names are not.
    /// </summary>
    [Fact]
    public void AReplacementIsNumberedAmongTheStringsThePrinterSent()
    {
        // Act
        NonFinitePatch? patch = Patch("""{"a":"NaN","v":nan,"b":["x",-inf,"y"],"c":{"d":Infinity},"e":null}""");

        // Assert
        patch.Should().NotBeNull();

        Text(patch).Should().Be("""{"a":"NaN","v":"NaN","b":["x","-Infinity","y"],"c":{"d":"Infinity"},"e":null}""");

        patch.Tokens.Should().Equal(new NonFiniteToken(1, "nan"),
                                    new NonFiniteToken(3, "-inf"),
                                    new NonFiniteToken(5, "Infinity"));
    }

    /// <summary>
    /// The Python SDK's shape: <c>json.dumps</c> puts a space after the colon and the comma, and
    /// <c>requests</c> posts it as the whole body - a trailing newline included here, which the HTTP
    /// transport's parse allows and the mended copy must still carry.
    /// </summary>
    [Fact]
    public void ThePythonShapeIsMendedUnderDefaultOptions()
    {
        // Act
        NonFinitePatch? patch = Patch("{\"event\": \"FILE_INFO\", \"data\": {\"layer_height\": NaN, \"max_layer_z\": Infinity}}\n");

        // Assert
        patch.Should().NotBeNull();

        Text(patch).Should().Be("{\"event\": \"FILE_INFO\", \"data\": {\"layer_height\": \"NaN\", \"max_layer_z\": \"Infinity\"}}\n");
    }

    /// <summary>
    /// A document that is nothing but the token is mended like any other. What it becomes is not a
    /// printer message, and saying so is the dispatcher's job rather than this type's.
    /// </summary>
    [Fact]
    public void ARootThatIsTheTokenIsMended()
    {
        // Act
        NonFinitePatch? patch = Patch("nan");

        // Assert
        patch.Should().NotBeNull();

        Text(patch).Should().Be("\"NaN\"");
    }

    /// <summary>
    /// A comment between the colon and the token is not looked through. Neither client sends one, so
    /// the message is refused as it was before rather than this type learning to read comments.
    /// </summary>
    [Fact]
    public void ACommentBeforeTheTokenLeavesItRefused()
    {
        // Act
        NonFinitePatch? patch = Patch("""{"v": /* c */ nan}""", Lenient);

        // Assert
        patch.Should().BeNull();
    }

    /// <summary>
    /// The token has to end at a delimiter. Nearly every other ending is refused by the reader on
    /// its own account; this is the one that is not, because the socket's options allow a second
    /// document to follow the first with nothing between them.
    /// </summary>
    [Fact]
    public void ATokenRunningIntoTheNextDocumentStaysRefused()
    {
        // Act
        NonFinitePatch? patch = Patch("""nan{"state":"IDLE"}""", Lenient);

        // Assert
        patch.Should().BeNull();
    }

    /// <summary>
    /// Comments anywhere else are the reader's business and do not disturb the mend.
    /// </summary>
    [Fact]
    public void CommentsElsewhereDoNotDisturbTheMend()
    {
        // Act
        NonFinitePatch? patch = Patch("/* a */ {\"a\":1, // b\n\"v\":nan /* c */ ,}", Lenient);

        // Assert
        patch.Should().NotBeNull();

        patch.Tokens.Should().Equal(new NonFiniteToken(0, "nan"));
    }

    /// <summary>
    /// Each mend costs an exception, so a sender must not be able to buy many of them with one
    /// message: up to the limit is mended, one more and the whole message is refused.
    /// </summary>
    [Theory]
    [InlineData(NonFiniteNumberPatcher.MaxReplacements, true)]
    [InlineData(NonFiniteNumberPatcher.MaxReplacements + 1, false)]
    public void TheNumberOfReplacementsIsBounded(int count, bool mended)
    {
        // Arrange
        string document = "{\"v\":[" + string.Join(',', Enumerable.Repeat("nan", count)) + "]}";

        // Act
        NonFinitePatch? patch = Patch(document);

        // Assert
        (patch is not null).Should().Be(mended);

        patch?.Tokens.Should().HaveCount(count);
    }
}
