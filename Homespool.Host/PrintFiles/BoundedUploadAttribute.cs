using System;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Homespool.Host.PrintFiles;

/// <summary>
/// Bounds what a form upload may buffer, at <see cref="PrintFileStorageOptions.MaxUploadBytes"/>
/// plus room for the rest of the form.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a filter rather than <c>[RequestSizeLimit]</c>.</b> Those attributes take a compile-time
/// constant and the cap is configuration, so the page carried <c>long.MaxValue</c> on both - which
/// removed Kestrel's ceiling and MVC's multipart ceiling and put nothing in their place. The check
/// the page does afterwards reads <c>IFormFile.Length</c>, and by then the body has already been
/// buffered and spilled to a temp file: the bytes are on disk before anything asks how many there
/// are. This reads the same option at request time and applies it before a byte is read.
/// </para>
/// <para>
/// <b>An authorization filter, and the ordering is the whole of why it works.</b> Razor Pages
/// validates antiforgery in an authorization filter at order 1000, and validating reads the form -
/// so a resource filter, which runs later, would set a limit after the buffering it meant to bound.
/// 900 is the order the framework's own <c>RequestSizeLimitAttribute</c> uses for the same reason.
/// Ordering that subtle is not something to assert by reading: <c>FilesPageUploadLimitTests</c>
/// drives a real host with a small configured cap and an oversized body.
/// </para>
/// <para>
/// <b>Streaming would be the other answer and is not available here.</b> The page binds
/// <see cref="IFormFile"/> deliberately - antiforgery has to read the form to find its token, so a
/// <c>MultipartReader</c> that consumed the body first would leave nothing to validate against. The
/// API's upload path has no such constraint and streams through <c>LengthLimitingStream</c> instead.
/// </para>
/// <para>
/// <b>This bound is the application's own, and does not depend on the proxy.</b> nginx caps the user
/// listener at 512 MB, which is why the gap was never an open door on the shipped stack - but it is
/// absent from anything not behind that proxy, which is exactly where a cap should not live.
/// </para>
/// <para>
/// <b>What it costs, stated rather than discovered later: the friendly refusal.</b> A body stopped
/// while it arrives ends as a bare 4xx - 413 under Kestrel, 400 where the multipart limit trips
/// first - not as the page's localised "larger than the limit" message, because there is no longer a
/// request to render a page onto. The <c>file.Length</c> check still produces that message, but only
/// for the narrow band between the cap and the cap plus <c>FormOverheadBytes</c>. That is inherent
/// rather than an oversight: refusing early and answering nicely are the same trade in opposite
/// directions, and only one of them bounds the disk.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false)]
public sealed class BoundedUploadAttribute : Attribute, IFilterFactory, IOrderedFilter
{
    /// <summary>
    /// Before Razor Pages' antiforgery filter at 1000, which reads the form and so must not be the
    /// first thing to touch the body.
    /// </summary>
    public const int BeforeAntiforgery = 900;

    public int Order { get; set; } = BeforeAntiforgery;

    public bool IsReusable
    {
        get { return true; }
    }

    public IFilterMetadata CreateInstance(IServiceProvider serviceProvider)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);

        return new BoundedUploadFilter(serviceProvider.GetRequiredService<IOptions<PrintFileStorageOptions>>());
    }
}

/// <summary>Applies the configured upload cap to the request, before anything reads the body.</summary>
internal sealed class BoundedUploadFilter : IAuthorizationFilter
{
    /// <summary>
    /// Room for the rest of the form beside the file: the multipart framing, the antiforgery token,
    /// and the sort fields the page posts alongside. Generous, because the cost of being wrong low is
    /// refusing a legitimate upload of exactly the permitted size.
    /// </summary>
    internal const long FormOverheadBytes = 64 * 1024;

    private readonly PrintFileStorageOptions _options;

    public BoundedUploadFilter(IOptions<PrintFileStorageOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _options = options.Value;
    }

    public void OnAuthorization(AuthorizationFilterContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        long limit = _options.MaxUploadBytes > long.MaxValue - FormOverheadBytes
            ? long.MaxValue
            : _options.MaxUploadBytes + FormOverheadBytes;

        IHttpMaxRequestBodySizeFeature? size = context.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();

        // Read-only once the body has been read, which is the state this filter exists to precede.
        if (size is not null && !size.IsReadOnly)
        {
            size.MaxRequestBodySize = limit;
        }

        // The server's ceiling bounds the bytes; this bounds what the multipart reader will assemble
        // out of them. Both, because either alone leaves one of the two paths unbounded.
        context.HttpContext.Features.Set<IFormFeature>(
            new FormFeature(context.HttpContext.Request, new FormOptions { MultipartBodyLengthLimit = limit }));
    }
}
