using Microsoft.AspNetCore.Html;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace Homespool.Host.Pages;

/// <summary>
/// The inline elements a localised sentence carries - <c>&lt;code&gt;</c>, <c>&lt;strong&gt;</c>, a
/// link - built so that their text and attributes are always encoded.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why sentences are built from parts at all.</b> A sentence with markup inside it is one resource
/// string with a <c>{0}</c> in it, so that a translator can move the fragment. The fragment goes in
/// through <c>HtmlLocaliser</c>, which encodes every argument that is not already
/// <see cref="IHtmlContent"/> - so a fragment has to be <see cref="IHtmlContent"/>, and this is the one
/// place that makes it.
/// </para>
/// <para>
/// <b>Why nothing takes markup as a string.</b> Markup composed at the call site is safe exactly as
/// long as every half of it is ours, and not every half is: a printer's stated firmware version is a
/// string the printer chose, and it reaches two of these sentences. Here a value can only arrive as
/// text, which <see cref="TagBuilder"/> encodes when the page is written, so the safety of a call site
/// is a property of the types rather than of whoever wrote it.
/// </para>
/// <para>
/// <b>The one way in for markup is an <see cref="IHtmlContent"/> argument</b>, for an element that
/// wraps another - <c>&lt;kbd&gt;&lt;samp&gt;</c>. The source test that refuses every raw-markup
/// call in the application allows this file that call, and only with that argument.
/// </para>
/// </remarks>
public static class Markup
{
    /// <summary>A literal a person would type or read back: a setting, a path, a value.</summary>
    public static IHtmlContent Code(string? text)
    {
        return Element("code", text);
    }

    /// <summary>The words in a sentence it hinges on.</summary>
    public static IHtmlContent Strong(string? text)
    {
        return Element("strong", text);
    }

    /// <summary>A name the reader finds elsewhere - a menu path, a screen.</summary>
    public static IHtmlContent Em(string? text)
    {
        return Element("em", text);
    }

    /// <summary>Something to type or press.</summary>
    public static IHtmlContent Kbd(string? text)
    {
        return Element("kbd", text);
    }

    /// <summary>Something to press that is itself another program's output - <c>&lt;kbd&gt;&lt;samp&gt;</c>.</summary>
    public static IHtmlContent Kbd(IHtmlContent content)
    {
        TagBuilder element = new("kbd");
        element.InnerHtml.AppendHtml(content);

        return element;
    }

    /// <summary>Text as another program shows it.</summary>
    public static IHtmlContent Samp(string? text)
    {
        return Element("samp", text);
    }

    /// <summary>
    /// A link. <paramref name="href"/> is an attribute value and encoded as one, so an <c>&amp;</c>
    /// between two query values is written as <c>&amp;amp;</c>.
    /// </summary>
    public static IHtmlContent Link(string? href, string? text, string? id = null)
    {
        TagBuilder element = new("a");
        element.MergeAttribute("href", href);

        if (id is not null)
        {
            element.MergeAttribute("id", id);
        }

        element.InnerHtml.Append(text ?? string.Empty);

        return element;
    }

    private static TagBuilder Element(string tag, string? text)
    {
        TagBuilder element = new(tag);
        element.InnerHtml.Append(text ?? string.Empty);

        return element;
    }
}
