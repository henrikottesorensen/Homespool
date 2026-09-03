using System;

namespace Homespool.Host.Exceptions;

/// <summary>
/// A file name the store will not take — the wrong extension, nothing usable left once the
/// directory part is stripped, or characters no name of ours may carry.
/// </summary>
/// <remarks>
/// <para>
/// <b>Derives from <see cref="ArgumentException"/> rather than replacing it</b>, because that is
/// what <c>UserFileStore</c> threw and what its callers catch. Existing <c>catch
/// (ArgumentException)</c> blocks keep working unchanged, and the tests that assert on the type keep
/// passing — the type is narrowed, not swapped.
/// </para>
/// <para>
/// It exists at all because these two messages reach a page: <c>Pages/Files/Index</c> puts the
/// caught exception's text into its status message, so a refused upload explains itself in whatever
/// language the exception was written in. A framework type cannot carry a resource key, so the
/// refusal needed one of its own.
/// </para>
/// </remarks>
public class PrintFileNameRejectedException : ArgumentException, ILocalisableError
{
    /// <summary>Set only by a refusal that has its own sentence; null leaves the two below.</summary>
    private readonly string? _resourceKey;

    /// <summary>A name whose extension is not one a printer would take.</summary>
    public PrintFileNameRejectedException(string fileName, string parameterName)
        : base($"'{fileName}' is not a file a printer would accept.", parameterName)
    {
        FileName = fileName;
    }

    /// <summary>A name with nothing left in it once its directory part is removed.</summary>
    public PrintFileNameRejectedException(string parameterName)
        : base(
            "File name is empty, or is a directory reference, once its directory part is removed.",
            parameterName)
    {
    }

    public PrintFileNameRejectedException()
    {
    }

    public PrintFileNameRejectedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    private PrintFileNameRejectedException(string message, string fileName, string parameterName, string resourceKey)
        : base(message, parameterName)
    {
        FileName = fileName;
        _resourceKey = resourceKey;
    }

    /// <summary>The name that was refused, when the refusal was about a specific one.</summary>
    public string? FileName { get; }

    /// <inheritdoc />
    public string ResourceKey =>
        _resourceKey ?? (FileName is null ? "Error_FileNameUnusable" : "Error_FileNameRejected");

    /// <inheritdoc />
    public object[] ResourceArguments => FileName is null ? [] : [FileName];

    /// <summary>
    /// The refusal that is about the characters rather than the extension, so it can say so.
    /// </summary>
    /// <remarks>
    /// A factory rather than a fourth constructor because the shape it wants —
    /// <c>(fileName, parameterName)</c> — is already taken by the extension refusal above, and two
    /// refusals distinguishable only by argument order is the kind of seam that gets called wrongly.
    /// </remarks>
    public static PrintFileNameRejectedException ForForbiddenCharacters(string fileName, string parameterName)
    {
        return new PrintFileNameRejectedException(
            $"'{fileName}' contains characters a file name may not have: quotes, angle brackets or control characters.",
            fileName, parameterName, "Error_FileNameCharacters");
    }
}
