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
    /// What to show for an error, in the current culture.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An exception that does not implement <see cref="ILocalisableError"/> answers with its own
    /// message, because that is strictly better than nothing and the alternative is a page that goes
    /// silent about a failure. It also means adding the interface to one more type is the whole
    /// change, with no call site to revisit.
    /// </para>
    /// <para>
    /// <b>Enum arguments go through the display seam.</b> A <see cref="PrinterStatus"/> handed
    /// straight to <c>string.Format</c> renders its English member name inside a Danish sentence -
    /// "Printeren er Printing" - which is worse than either language on its own. This is the only
    /// argument type given special treatment, and it is given it because the seam for translating
    /// one already exists.
    /// </para>
    /// </remarks>
    public string For(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);

        if (error is not ILocalisableError localisable)
        {
            return error.Message;
        }

        object[] arguments = [.. localisable.ResourceArguments
                                            .Select(argument => argument is PrinterStatus status ?
                                                        _statuses.For(status) :
                                                        argument)];

        return _localiser[localisable.ResourceKey, arguments].Value;
    }
}
