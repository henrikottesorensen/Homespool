using System;
using System.Linq;

using Microsoft.Extensions.Localization;

using Homespool.Host.Exceptions;
using Homespool.Model;

namespace Homespool.Host.Localisation;

/// <summary>
/// Turns a thrown error into the sentence a page shows.
/// </summary>
/// <remarks>
/// <para>
/// <b>Exists because <c>e.Message</c> was the sentence on eight pages and nobody had noticed.</b>
/// Assigning an exception's message straight to a status message reads as harmless plumbing and is
/// in fact a rendering decision: it publishes text written in a service, in English, to whoever
/// pressed the button. Three localisation audits walked past it because they searched page models
/// for literals, and these literals live in the type being thrown.
/// </para>
/// <para>
/// The English message is deliberately <i>not</i> the fallback when a key is missing. A resource
/// miss shows the key, which is loud and gets fixed; falling back to <see cref="Exception.Message"/>
/// would make a missing translation indistinguishable from a working one for every English reader,
/// which is the exact failure mode this whole phase kept walking into.
/// </para>
/// </remarks>
public sealed class ErrorText
{
    private readonly IStringLocalizer<SharedResource> _localiser;
    private readonly PrinterStatusText _statuses;

    public ErrorText(IStringLocalizer<SharedResource> localiser, PrinterStatusText statuses)
    {
        _localiser = localiser;
        _statuses = statuses;
    }

    /// <summary>
    /// What to show for a sentence a service chose but did not say.
    /// </summary>
    /// <remarks>
    /// The same journey as <see cref="For(Exception)"/> without a throw in it: a result carrying a
    /// <see cref="MessageKey"/> is a service saying <i>which</i> sentence, leaving <i>in what
    /// language</i> to whoever renders it.
    /// </remarks>
    public string For(MessageKey message)
    {
        ArgumentNullException.ThrowIfNull(message);

        return _localiser[message.Key, Translated(message.Arguments)].Value;
    }

    /// <summary>
    /// What to show for an error, in the current culture.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An exception that does not implement <see cref="ILocalisableError"/> answers with its own
    /// message, because that is strictly better than nothing and the alternative is a page that goes
    /// silent about a failure. It also means adding the interface to one more type is the whole
    /// change, with no call site to revisit.
    /// </para>
    /// </remarks>
    public string For(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);

        if (error is not ILocalisableError localisable)
        {
            return error.Message;
        }

        return _localiser[localisable.ResourceKey, Translated(localisable.ResourceArguments)].Value;
    }

    /// <summary>
    /// Arguments as they should appear in a sentence, which for an enum is not its name.
    /// </summary>
    /// <remarks>
    /// <b>The one argument type given special treatment.</b> A <see cref="PrinterStatus"/> handed
    /// straight to <c>string.Format</c> renders its English member name inside a Danish sentence -
    /// "Printeren er Printing" - which is worse than either language on its own. It gets the
    /// treatment because a seam for translating one already exists; everything else is data and is
    /// reproduced as it stands.
    /// </remarks>
    private object[] Translated(object[] arguments)
    {
        return [.. arguments.Select(argument => argument is PrinterStatus status ?
                                        _statuses.For(status) :
                                        argument)];
    }
}
