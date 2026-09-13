using System.IO;
using System.Text.Encodings.Web;

using AwesomeAssertions;

using Microsoft.AspNetCore.Html;
using Microsoft.AspNetCore.Mvc.Localization;
using Microsoft.Extensions.DependencyInjection;

using Homespool.Host.Localisation;
using Homespool.Host.Pages;

namespace Homespool.Host.Test;

/// <summary>
/// A value handed to a sentence's fragment reaches the page as text: every <see cref="Markup"/>
/// element encodes what it is given, and so does the localiser those elements are passed to.
/// </summary>
/// <remarks>
/// <para>
/// <b>These are what the source scan leans on.</b> <c>RawHtmlEncodingTests</c> refuses every way of
/// writing a string as markup, which is only a guarantee if the one sanctioned way of putting an
/// element in a sentence really does encode. A helper that appended its text as HTML would pass that
/// scan, and fail here.
/// </para>
/// <para>
/// <b>The payload is the one that mattered.</b> A printer's stated firmware version is a string the
/// printer chose, and reached two of these sentences as markup; an element, an attribute break-out
/// and an ampersand are the three things an encoder has to get right.
/// </para>
/// </remarks>
public sealed class MarkupTests
{
    private const string Payload = "6.6.0+<img src=x onerror=alert(1)>&\"";

    /// <summary>
    /// The payload as the encoder writes it - which also encodes <c>+</c> - checked below to carry none
    /// of the characters that matter, so the expectations are not simply the encoder agreeing with itself.
    /// </summary>
    private static readonly string Encoded = HtmlEncoder.Default.Encode(Payload);

    [Fact]
    public void TheExpectedFormCarriesNothingThatCouldBeMarkup()
    {
        // Assert
        Encoded.Should().NotContainAny("<", ">", "\"").And.Contain("&lt;img").And.Contain("&amp;");
    }

    [Fact]
    public void EveryElementEncodesItsText()
    {
        // Act & Assert
        Render(Markup.Code(Payload)).Should().Be($"<code>{Encoded}</code>");
        Render(Markup.Strong(Payload)).Should().Be($"<strong>{Encoded}</strong>");
        Render(Markup.Em(Payload)).Should().Be($"<em>{Encoded}</em>");
        Render(Markup.Kbd(Payload)).Should().Be($"<kbd>{Encoded}</kbd>");
        Render(Markup.Samp(Payload)).Should().Be($"<samp>{Encoded}</samp>");
    }

    [Fact]
    public void ALinkEncodesItsTextAndItsAttributes()
    {
        // Act
        string link = Render(Markup.Link($"/x?a=1&b=\" onmouseover=\"{Payload}", Payload, id: Payload));

        // Assert
        link.Should().Be($"<a href=\"/x?a=1&amp;b=&quot; onmouseover=&quot;{Encoded}\" id=\"{Encoded}\">{Encoded}</a>",
                         "a quote in an attribute value is what would end the attribute and start a handler");
    }

    [Fact]
    public void AnElementInsideAnotherKeepsItsOwnEncoding()
    {
        // Act
        string keys = Render(Markup.Kbd(Markup.Samp(Payload)));

        // Assert
        keys.Should().Be($"<kbd><samp>{Encoded}</samp></kbd>",
                         "the outer element takes the inner one as markup, and the inner one has already encoded its text");
    }

    [Fact]
    public void NoTextIsAnEmptyElement()
    {
        // Act & Assert
        Render(Markup.Code(null)).Should().Be("<code></code>");
    }

    /// <summary>
    /// The other half of the arrangement: a value passed to the localiser as a plain argument is
    /// encoded too, so forgetting the element costs the formatting and never the encoding.
    /// </summary>
    [Fact]
    public void TheHtmlLocaliserEncodesAPlainArgumentAndKeepsAnElementWhole()
    {
        // Arrange
        IHtmlLocalizer<SharedResource> localiser = HtmlLocaliser();

        // Act
        string plain = Render(localiser["Printer_PlaintextBody", Payload]);
        string element = Render(localiser["Printer_PlaintextBody", Markup.Code(Payload)]);

        // Assert
        plain.Should().Contain(Encoded).And.NotContain("<img");
        element.Should().Contain($"<code>{Encoded}</code>").And.NotContain("<img");
    }

    /// <summary>The HTML localiser, resolved from the application's own registration.</summary>
    private static IHtmlLocalizer<SharedResource> HtmlLocaliser()
    {
        ServiceCollection services = new();
        services.AddLogging();
        services.AddHomespoolLocalisation();

        return services.BuildServiceProvider().GetRequiredService<IHtmlLocalizer<SharedResource>>();
    }

    private static string Render(IHtmlContent content)
    {
        using StringWriter writer = new();
        content.WriteTo(writer, HtmlEncoder.Default);

        return writer.ToString();
    }
}
