using System;
using System.Collections.Generic;
using System.Linq;

using AwesomeAssertions;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

using Serilog.Events;
using Serilog.Parsing;

using Homespool.Host.Services;

namespace Homespool.Host.Test;

/// <summary>
/// Every property of a log event comes out printable, whatever shape it has and whoever logged it -
/// the one place that holds for the lines no call site of ours writes.
/// </summary>
/// <remarks>
/// Driven directly: an enricher is a method over an event, so nothing here needs a logging pipeline.
/// That the host registers it is an end-to-end matter, pinned by <c>PrintableLogTests</c> there. The
/// escapes are written rather than pasted, as in <c>LogTextTests</c>.
/// </remarks>
public class PrintableLogEnricherTests
{
    /// <summary>
    /// One of each kind the JSON formatter lets through as itself, and the two it does escape - which
    /// a reader who decodes the line gets back.
    /// </summary>
    [Theory]
    [InlineData("\u000A")]
    [InlineData("\u001B")]
    [InlineData("\u007F")]
    [InlineData("\u0085")]
    [InlineData("\u00AD")]
    [InlineData("\u200B")]
    [InlineData("\u200E")]
    [InlineData("\u202E")]
    [InlineData("\u2028")]
    [InlineData("\u2029")]
    [InlineData("\u2060")]
    [InlineData("\uFEFF")]
    public void AnUnprintableCharacterIsReplaced(string character)
    {
        PrintableLogEnricher.Printable("MK4" + character + "IS").Should().Be("MK4\uFFFDIS");
    }

    /// <summary>Never narrower than the rule names are held to: what that refuses, this replaces.</summary>
    [Fact]
    public void EverythingPrintableTextRefusesIsReplacedHere()
    {
        IEnumerable<char> refused = Enumerable.Range(0, char.MaxValue + 1)
                                              .Select(code => (char)code)
                                              .Where(character => !char.IsSurrogate(character) && PrintableText.IsUnprintable(character));

        refused.Should().OnlyContain(character => PrintableLogEnricher.Printable(character.ToString()) == "\uFFFD");
    }

    /// <summary>
    /// Judged a character at a time, not a <see cref="char"/> at a time: both halves of an emoji are
    /// surrogates, and a rule that replaced surrogates would replace every one of them.
    /// </summary>
    [Fact]
    public void ACharacterOutsideTheBasicPlaneSurvivesAndAHalfOfOneDoesNot()
    {
        string emoji = char.ConvertFromUtf32(0x1F600);

        PrintableLogEnricher.Printable("ok " + emoji).Should().Be("ok " + emoji);
        PrintableLogEnricher.Printable("half " + emoji[0]).Should().Be("half \uFFFD");
        PrintableLogEnricher.Printable(emoji[1] + " half").Should().Be("\uFFFD half");
    }

    /// <summary>A format character outside the basic plane is one character, and gets one replacement.</summary>
    [Fact]
    public void AnInvisibleCharacterOutsideTheBasicPlaneIsReplacedOnce()
    {
        PrintableLogEnricher.Printable("tag" + char.ConvertFromUtf32(0xE0001) + "ged").Should().Be("tag\uFFFDged");
    }

    [Theory]
    [InlineData("15715-4842441651816441")]
    [InlineData("Br\u00E4cket-2.gcode")]
    [InlineData("\u65E5\u672C\u8A9E")]
    public void AnOrdinaryValueComesBackAsItself(string value)
    {
        PrintableLogEnricher.Printable(value).Should().BeSameAs(value);
    }

    // ---- every shape a property takes ----
    [Fact]
    public void AStringPropertyIsReplacedAndTheOthersAreLeftAsTheyWere()
    {
        ScalarValue id = new(7);
        LogEvent logEvent = Event(("UserAgent", new ScalarValue("curl\u001B[2J")), ("PrinterId", id));

        new PrintableLogEnricher().Enrich(logEvent, null!);

        Text(logEvent, "UserAgent").Should().Be("curl\uFFFD[2J");
        logEvent.Properties["PrinterId"].Should().BeSameAs(id);
    }

    [Fact]
    public void TheRenderedMessageCarriesTheReplacement()
    {
        LogEvent logEvent = Event(("UserAgent", new ScalarValue("curl\u202E")));

        new PrintableLogEnricher().Enrich(logEvent, null!);

        logEvent.RenderMessage().Should().Contain("curl\uFFFD").And.NotContain("\u202E");
    }

    [Fact]
    public void ASequenceAStructureAndADictionaryAreWalked()
    {
        LogEvent logEvent = Event(
            ("List", new SequenceValue([new ScalarValue("a\u2028"), new ScalarValue("b")])),
            ("Shape", new StructureValue([new LogEventProperty("Name", new ScalarValue("c\u0085"))], "Tag")),
            ("Map", new DictionaryValue([new(new ScalarValue("k\u200B"), new ScalarValue("v\u007F"))])));

        new PrintableLogEnricher().Enrich(logEvent, null!);

        ((SequenceValue)logEvent.Properties["List"]).Elements.Select(Text).Should().Equal("a\uFFFD", "b");

        StructureValue shape = (StructureValue)logEvent.Properties["Shape"];
        shape.TypeTag.Should().Be("Tag");
        Text(shape.Properties.Single().Value).Should().Be("c\uFFFD");

        KeyValuePair<ScalarValue, LogEventPropertyValue> entry = ((DictionaryValue)logEvent.Properties["Map"]).Elements.Single();
        Text(entry.Key).Should().Be("k\uFFFD");
        Text(entry.Value).Should().Be("v\uFFFD");
    }

    /// <summary>
    /// A value that is not a string is written through its <c>ToString</c>, so that is what has to be
    /// printable - a header collection carries a stranger's characters as well as any string does.
    /// </summary>
    [Fact]
    public void AnObjectIsJudgedByWhatItWouldBeWrittenAs()
    {
        LogEvent logEvent = Event(("Header", new ScalarValue(new StringValues("cross-site\u001B[2J"))));

        new PrintableLogEnricher().Enrich(logEvent, null!);

        Text(logEvent, "Header").Should().Be("cross-site\uFFFD[2J");
    }

    /// <summary>And one whose text is already fine stays the object it was, for a sink that cares about the type.</summary>
    [Fact]
    public void AnObjectThatIsAlreadyPrintableIsNotTurnedIntoAString()
    {
        ScalarValue path = new(new PathString("/api/v1/files"));
        LogEvent logEvent = Event(("Path", path));

        new PrintableLogEnricher().Enrich(logEvent, null!);

        logEvent.Properties["Path"].Should().BeSameAs(path);
    }

    [Fact]
    public void AnEventWithNothingToReplaceKeepsEveryValueItHad()
    {
        (string, LogEventPropertyValue)[] properties =
        [
            ("Name", new ScalarValue("Anna S\u00F8rensen")),
            ("Nothing", new ScalarValue(null)),
            ("When", new ScalarValue(DateTimeOffset.UnixEpoch)),
            ("List", new SequenceValue([new ScalarValue("a")])),
        ];
        LogEvent logEvent = Event(properties);

        new PrintableLogEnricher().Enrich(logEvent, null!);

        foreach ((string name, LogEventPropertyValue value) in properties)
        {
            logEvent.Properties[name].Should().BeSameAs(value);
        }
    }

    private static LogEvent Event(params (string name, LogEventPropertyValue value)[] properties)
    {
        string template = string.Join(" ", properties.Select(property => "{" + property.name + "}"));

        return new LogEvent(DateTimeOffset.UnixEpoch,
                            LogEventLevel.Information,
                            exception: null,
                            new MessageTemplateParser().Parse(template),
                            properties.Select(property => new LogEventProperty(property.name, property.value)));
    }

    private static string? Text(LogEvent logEvent, string name)
    {
        return Text(logEvent.Properties[name]);
    }

    private static string? Text(LogEventPropertyValue value)
    {
        return ((ScalarValue)value).Value as string;
    }
}
