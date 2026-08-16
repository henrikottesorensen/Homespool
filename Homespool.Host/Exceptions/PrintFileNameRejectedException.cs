using System;

namespace Homespool.Host.Exceptions;

/// <summary>
/// A file name the store will not take — the wrong extension, or nothing usable left once the
/// directory part is stripped.
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

    /// <summary>The name that was refused, when the refusal was about a specific one.</summary>
    public string? FileName { get; }

    /// <inheritdoc />
    public string ResourceKey => FileName is null ? "Error_FileNameUnusable" : "Error_FileNameRejected";

    /// <inheritdoc />
    public object[] ResourceArguments => FileName is null ? [] : [FileName];
}
