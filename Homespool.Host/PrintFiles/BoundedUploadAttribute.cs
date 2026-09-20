using System;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Homespool.Host.PrintFiles;

/// <summary>
/// Bounds what a form upload may buffer: at <see cref="PrintFileStorageOptions.MaxUploadBytes"/>
/// plus room for the rest of the form, or at a limit the declaration names.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two forms, and they deliberately bound different quantities.</b> <c>[BoundedUpload]</c> takes
/// <see cref="PrintFileStorageOptions.MaxUploadBytes"/> from configuration and adds
/// <c>FormOverheadBytes</c> on top, because that setting describes a <i>file</i> and the request
/// carries multipart framing and form fields beside it. <c>[BoundedUpload(n)]</c> is the request
/// ceiling itself, used as written with nothing added: an endpoint naming its own number is
/// describing the whole request rather than a file inside it, and quietly granting it 64 KB more
/// than it asked for is the kind of surprise this attribute exists to remove. Prefer the first
/// wherever the payload is a print file, so one setting still moves every endpoint carrying it
/// together - at the next restart, per the paragraph below.
/// </para>
/// <para>
/// <b>That cap is read once, when the endpoint's filter is built, so a saved change is obeyed at the
/// next restart.</b> <see cref="IsReusable"/> is <see langword="true"/>, so MVC calls
/// <see cref="CreateInstance"/> at the first request an endpoint serves and caches the filter on that
/// endpoint's action descriptor; the filter holds the <see cref="PrintFileStorageOptions"/> instance
/// the monitor had at that moment, and a configuration reload replaces that instance rather than
/// changing it. <b>The other readers of the setting take <c>IOptionsSnapshot</c> and do follow a
/// save</b>, so between a save and a restart a page refuses by the new number while the body is still
/// allowed to arrive under the old one - which is why <c>EditableSettings</c> grades
/// <see cref="PrintFileStorageOptions.MaxUploadBytes"/> <c>Restart</c> rather than have the Settings
/// page promise a bound that is only half in force. <b>A snapshot cannot be taken here instead</b>:
/// <see cref="CreateInstance"/> is handed the first request's <c>RequestServices</c>, so anything
/// scoped resolved in it would outlive the scope it came from.
/// </para>
/// <para>
/// <b>Only a signed-in caller is granted either ceiling; a stranger gets none.</b> A signed-out request
/// that could carry a body is refused here, before antiforgery reads the form, with its ceiling at
/// zero. That matters on a page that is itself anonymous, like the home page: otherwise the raise
/// would reach strangers too, and antiforgery would spool their bodies to disk before the handler
/// could say no. "Signed in" means <see cref="HttpContext.User"/> as it stands when this runs, so an
/// endpoint authenticated under a scheme other than the default needs an <c>[Authorize]</c> naming it,
/// because the authorization middleware is what puts that principal in place.
/// </para>
/// <para>
/// <b>It binds where it is declared and nowhere else, and nothing checks that it was.</b> An endpoint
/// that takes a print file without this attribute is not bounded by
/// <see cref="PrintFileStorageOptions.MaxUploadBytes"/> at all - it falls back to Kestrel's own
/// request default, which is unrelated to the configured cap and moves with neither it nor the proxy.
/// No build or test fails over the omission, so a handler that accepts an upload has to carry this
/// beside it as a matter of course: the dialog that advertises the configured cap is not the thing
/// that enforces it.
/// </para>
/// <para>
/// <b>Why a filter rather than <c>[RequestSizeLimit]</c>.</b> Those attributes take a compile-time
/// constant and the cap is configuration, so the page carried <c>long.MaxValue</c> on both - which
/// removed Kestrel's ceiling and MVC's multipart ceiling and put nothing in their place. The check
/// the page does afterwards reads <c>IFormFile.Length</c>, and by then the body has already been
/// buffered and spilled to a temp file: the bytes are on disk before anything asks how many there
/// are. This takes the same option from configuration and applies it before a byte is read.
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

    /// <summary>Bounds the request at the configured upload cap plus form overhead.</summary>
    public BoundedUploadAttribute()
    {
    }

    /// <summary>Bounds the request at <paramref name="maxBytes"/> exactly.</summary>
    /// <param name="maxBytes">
    /// The request ceiling in bytes. Nothing is added to it - see the remarks on the two forms.
    /// </param>
    public BoundedUploadAttribute(long maxBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);

        MaxBytes = maxBytes;
    }

    /// <summary>
    /// The declared ceiling, or <c>null</c> to take the configured cap at request time.
    /// </summary>
    /// <remarks>
    /// Not settable as a named argument, deliberately: a nullable is not a legal attribute argument
    /// type, and the two constructors say which form is meant without a sentinel value to read.
    /// </remarks>
    internal long? MaxBytes { get; }

    public int Order { get; set; } = BeforeAntiforgery;

    public bool IsReusable
    {
        get { return true; }
    }

    public IFilterMetadata CreateInstance(IServiceProvider serviceProvider)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);

        return new BoundedUploadFilter(serviceProvider.GetRequiredService<IOptionsMonitor<PrintFileStorageOptions>>(),
                                       MaxBytes);
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

    private readonly long? _maxBytes;

    public BoundedUploadFilter(IOptionsMonitor<PrintFileStorageOptions> options, long? maxBytes)
    {
        ArgumentNullException.ThrowIfNull(options);

        // The value, not the monitor: this filter is cached per endpoint, so the cap it enforces is
        // the one in force when that endpoint first ran and holds until the process restarts.
        // MaxUploadBytes is graded Restart to say so - the attribute's remarks have the whole of it.
        _options = options.CurrentValue;
        _maxBytes = maxBytes;
    }

    public void OnAuthorization(AuthorizationFilterContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.HttpContext.User.Identity?.IsAuthenticated != true)
        {
            RefuseStranger(context);

            return;
        }

        // A declared ceiling is the request's own and is used as written; the configured cap
        // describes a file, so it needs room for the framing and fields that travel with it.
        long limit = _maxBytes ?? (_options.MaxUploadBytes > long.MaxValue - FormOverheadBytes ?
            long.MaxValue :
            _options.MaxUploadBytes + FormOverheadBytes);

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

    /// <summary>
    /// A signed-out caller has no upload to make, so they get no body at all, and a request that could
    /// carry one is refused before anything reads it.
    /// </summary>
    /// <remarks>
    /// <b>The ceiling goes to zero as well as the request being refused.</b> Kestrel drains a body the
    /// application never read, discarding it, up to <see cref="IHttpMaxRequestBodySizeFeature"/>'s
    /// limit - so a refusal alone still accepts that many bytes off the network, and a limit already
    /// raised would make it the whole configured cap. At zero the connection closes instead. A
    /// <c>GET</c> or <c>HEAD</c> passes, because the page carrying this may itself be anonymous.
    /// </remarks>
    private static void RefuseStranger(AuthorizationFilterContext context)
    {
        HttpContext http = context.HttpContext;

        IHttpMaxRequestBodySizeFeature? size = http.Features.Get<IHttpMaxRequestBodySizeFeature>();

        if (size is not null && !size.IsReadOnly)
        {
            size.MaxRequestBodySize = 0;
        }

        if (!HttpMethods.IsGet(http.Request.Method) && !HttpMethods.IsHead(http.Request.Method))
        {
            context.Result = new ForbidResult();
        }
    }
}
