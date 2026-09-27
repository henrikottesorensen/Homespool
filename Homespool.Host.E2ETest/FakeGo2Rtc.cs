using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;

namespace Homespool.Host.E2ETest;

/// <summary>
/// A stand-in go2rtc sidecar: its HTTP API on one loopback port and its RTSP side on another, with
/// state a test can script and inspect.
/// </summary>
/// <remarks>
/// <para>
/// <b>A fake in the Meszaros sense, not a mock, and on real sockets for three reasons.</b> The codec
/// probe opens a raw TCP connection that no <c>HttpMessageHandler</c> would see; a live MJPEG stream
/// and a viewer leaving it only behave for real on a real connection; and the sidecar credential is
/// checked as it actually arrives, so a client that stopped sending it fails here as it would there.
/// </para>
/// <para>
/// <b>It imitates what has been measured against go2rtc and nothing it would have to guess.</b>
/// Where Homespool reads only success or failure, the failure's exact status is this fake's own and
/// says so. The facts it does reproduce: every path off <c>allow_paths</c> is a bare 404 - read
/// from <c>compose.yaml</c> rather than copied, so an endpoint added to <c>Go2RtcClient</c> without
/// being allowed fails here the way it would on a deployment; <c>DELETE /api/streams?name=</c>
/// answers 200 and removes nothing, only <c>?src=</c> removes; a configuration write merges into
/// the document, and every registered stream survives it and the restart after it; a WebRTC offer
/// the camera's codecs cannot meet is refused by a body saying <c>codecs not matched</c>, not by
/// its status; a camera that is not there is answered by <c>frame.jpeg</c> and <c>stream.mjpeg</c>
/// alike with a 200 and nothing in it; and <c>stream.mjpeg</c>'s frames carry their Huffman tables,
/// because Homespool's image adds them to the frames a USB camera sends without.
/// </para>
/// <para>
/// <b>A fake encodes a belief about go2rtc, and a belief can be wrong</b> - an empty
/// <c>streams: {}</c> refusing every stream was one, and a configuration write taking the streams
/// with it was another. The contract tests run the same assertions against this and against the
/// real image, so the two cannot drift apart unnoticed; this is what lets the code on Homespool's
/// side of the API be exercised where no real sidecar runs.
/// </para>
/// <para>
/// <b>Cameras are keyed by the source the sidecar is handed</b>, password included, because that is
/// the only thing the real sidecar knows a camera by. A source nobody declared is a camera that is
/// not there: it registers, and produces nothing - which is what a TEST-NET address is.
/// </para>
/// </remarks>
public sealed class FakeGo2Rtc : IAsyncDisposable
{
    public const string Username = "homespool";
    public const string Password = "fake-sidecar-password"; // betterleaks:allow - the credential of a sidecar that exists only for the length of a test

    /// <summary>go2rtc's own answer for a stream name it does not hold, from a handler that exists.</summary>
    private const string StreamNotFound = "stream not found";

    /// <summary>Go's mux, answering a path no handler was registered for.</summary>
    private const string PageNotFound = "404 page not found";

    private const string MjpegBoundary = "frame";

    private static readonly TimeSpan MjpegFrameInterval = TimeSpan.FromMilliseconds(50);

    private readonly Lock _gate = new();
    private readonly Dictionary<string, string> _streams = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FakeCamera> _cameras = new(StringComparer.Ordinal);
    private readonly HashSet<string> _refusedSources = new(StringComparer.Ordinal);
    private readonly List<string> _requests = [];
    private readonly List<string> _configWrites = [];
    private readonly List<string> _offers = [];
    private readonly IReadOnlySet<string> _allowedPaths;
    private readonly TcpListener _rtsp;
    private readonly CancellationTokenSource _stopping = new();

    private WebApplication? _api;
    private Task _rtspLoop = Task.CompletedTask;
    private string _config = string.Empty;
    private int _restarts;
    private int _describes;
    private int _openMjpegStreams;
    private bool _configurationUnwritable;
    private CancellationTokenSource _cut = new();

    private FakeGo2Rtc(IReadOnlySet<string> allowedPaths)
    {
        _allowedPaths = allowedPaths;
        _rtsp = new TcpListener(IPAddress.Loopback, 0);
    }

    /// <summary>Where the HTTP API answers, as <c>Cameras:StreamServerBaseUrl</c> wants it.</summary>
    public Uri BaseAddress { get; private set; } = null!;

    /// <summary>The RTSP side's port, as <c>Cameras:StreamServerRtspPort</c> wants it.</summary>
    public int RtspPort => ((IPEndPoint)_rtsp.LocalEndpoint).Port;

    /// <summary>
    /// The configuration document, as <c>GET /api/config</c> hands it over. Settable so a test can
    /// start from a sidecar that is already configured.
    /// </summary>
    public string Config
    {
        get
        {
            lock (_gate)
            {
                return _config;
            }
        }

        set
        {
            lock (_gate)
            {
                _config = value;
            }
        }
    }

    /// <summary>The streams the sidecar holds, stream name to source.</summary>
    public IReadOnlyDictionary<string, string> Streams
    {
        get
        {
            lock (_gate)
            {
                return new Dictionary<string, string>(_streams, StringComparer.Ordinal);
            }
        }
    }

    /// <summary>Every API request that got past the credential, as <c>METHOD /path?query</c>.</summary>
    public IReadOnlyList<string> Requests
    {
        get
        {
            lock (_gate)
            {
                return [.. _requests];
            }
        }
    }

    /// <summary>Every document written with <c>PATCH /api/config</c>, in order.</summary>
    public IReadOnlyList<string> ConfigWrites
    {
        get
        {
            lock (_gate)
            {
                return [.. _configWrites];
            }
        }
    }

    /// <summary>Every WebRTC offer's session description, as the sidecar received it.</summary>
    public IReadOnlyList<string> Offers
    {
        get
        {
            lock (_gate)
            {
                return [.. _offers];
            }
        }
    }

    /// <summary>How many times the sidecar was restarted.</summary>
    public int Restarts => Volatile.Read(ref _restarts);

    /// <summary>How many RTSP <c>DESCRIBE</c>s were answered, of any kind.</summary>
    public int Describes => Volatile.Read(ref _describes);

    /// <summary>MJPEG streams currently held open by a client.</summary>
    public int OpenMjpegStreams => Volatile.Read(ref _openMjpegStreams);

    /// <summary>
    /// Drops every open MJPEG stream mid-frame, the way a restart of the sidecar or of Homespool
    /// does: the connection goes, with no closing boundary and no error in the stream.
    /// </summary>
    public async Task CutMjpegStreamsAsync()
    {
        CancellationTokenSource cut;

        lock (_gate)
        {
            cut = _cut;
            _cut = new CancellationTokenSource();
        }

        // Not disposed: a stream opening in this same moment may still be linking to its token.
        await cut.CancelAsync();
    }

    /// <summary>Starts both listeners on loopback ports of their own.</summary>
    public static async Task<FakeGo2Rtc> StartAsync()
    {
        FakeGo2Rtc sidecar = new(AllowedPathsFromCompose());

        // Empty rather than slim: a slim builder reads appsettings.json from the working directory,
        // which in a test is Homespool's own - and its AllowedHosts answers 400 to a request for
        // 127.0.0.1 before this code sees it.
        WebApplicationBuilder builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions());
        builder.WebHost.UseKestrelCore().UseUrls("http://127.0.0.1:0");

        WebApplication api = builder.Build();
        api.Run(sidecar.HandleAsync);

        await api.StartAsync();

        string address = api.Services.GetRequiredService<IServer>()
                            .Features.GetRequiredFeature<IServerAddressesFeature>()
                            .Addresses.Single();

        sidecar._api = api;
        sidecar.BaseAddress = new Uri(address);

        sidecar._rtsp.Start();
        sidecar._rtspLoop = sidecar.AcceptRtspAsync(sidecar._stopping.Token);

        return sidecar;
    }

    /// <summary>
    /// Points a host at this sidecar, with the credential it expects.
    /// </summary>
    public void ApplyTo(HomespoolFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);

        factory.ConfigurationOverrides["Cameras:ApiUsername"] = Username;
        factory.ConfigurationOverrides["Cameras:ApiPassword"] = Password;
        factory.ConfigurationOverrides["Cameras:StreamServerBaseUrl"] = BaseAddress.ToString().TrimEnd('/');
        factory.ConfigurationOverrides["Cameras:StreamServerRtspPort"] =
            RtspPort.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Declares what the camera behind <paramref name="source"/> does.</summary>
    public void AddCamera(string source, FakeCamera camera)
    {
        lock (_gate)
        {
            _cameras[source] = camera;
        }
    }

    /// <summary>Makes <c>PUT /api/streams</c> refuse <paramref name="source"/>, as go2rtc refuses <c>exec:</c>.</summary>
    public void RefuseSource(string source)
    {
        lock (_gate)
        {
            _refusedSources.Add(source);
        }
    }

    /// <summary>
    /// Makes <c>PUT /api/streams</c> fail to save, as go2rtc does when it cannot patch or write
    /// go2rtc.yaml: the stream exists until a restart, and the answer is a 400 about the file.
    /// </summary>
    public void FailConfigurationWrites()
    {
        lock (_gate)
        {
            _configurationUnwritable = true;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync();
        _cut.Dispose();
        _rtsp.Dispose();

        try
        {
            await _rtspLoop;
        }
        catch (OperationCanceledException)
        {
            // The loop ends by being cancelled.
        }

        if (_api is not null)
        {
            await _api.StopAsync();
            await _api.DisposeAsync();
        }

        _stopping.Dispose();
    }

    /// <summary>
    /// The sidecar's <c>allow_paths</c>, read out of the command line <c>compose.yaml</c> gives it.
    /// </summary>
    /// <remarks>
    /// The first uncommented line carrying the key, because the developer's alternative below it is
    /// commented out and carries none. Throws rather than allowing everything: a fake that cannot find
    /// the list would quietly stop catching the one mistake it reads the list to catch.
    /// </remarks>
    private static HashSet<string> AllowedPathsFromCompose([CallerFilePath] string thisFile = "")
    {
        string compose = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(thisFile))!, "compose.yaml");

        foreach (string line in File.ReadLines(compose))
        {
            if (line.TrimStart().StartsWith('#'))
            {
                continue;
            }

            Match match = Regex.Match(line, "\"allow_paths\":\\[([^\\]]*)\\]");

            if (match.Success)
            {
                return match.Groups[1].Value
                                      .Split(',')
                                      .Select(path => path.Trim().Trim('"'))
                                      .ToHashSet(StringComparer.Ordinal);
            }
        }

        throw new InvalidOperationException($"No allow_paths for the sidecar in {compose}.");
    }

    private async Task HandleAsync(HttpContext context)
    {
        HttpRequest request = context.Request;

        if (!_allowedPaths.Contains(request.Path.Value ?? string.Empty))
        {
            await AnswerAsync(context, StatusCodes.Status404NotFound, PageNotFound);
            return;
        }

        if (!IsAuthorised(request))
        {
            context.Response.Headers.WWWAuthenticate = "Basic realm=\"go2rtc\"";
            await AnswerAsync(context, StatusCodes.Status401Unauthorized, "Unauthorized");
            return;
        }

        lock (_gate)
        {
            _requests.Add($"{request.Method} {request.Path}{request.QueryString}");
        }

        switch (request.Path.Value)
        {
            case "/api/streams":
                await StreamsAsync(context);
                break;

            case "/api/frame.jpeg":
                await FrameAsync(context);
                break;

            case "/api/stream.mjpeg":
                await MjpegAsync(context);
                break;

            case "/api/webrtc":
                await WebRtcAsync(context);
                break;

            case "/api/config":
                await ConfigAsync(context);
                break;

            case "/api/restart":
                await RestartAsync(context);
                break;

            case "/api/ffmpeg/devices":
                // What a sidecar with no device grant answers - the listing is served only when the
                // container can open a video device.
                await AnswerAsync(context, StatusCodes.Status404NotFound, "no sources");
                break;

            default:
                await AnswerAsync(context, StatusCodes.Status404NotFound, PageNotFound);
                break;
        }
    }

    private static bool IsAuthorised(HttpRequest request)
    {
        string expected = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{Username}:{Password}"));

        return string.Equals(request.Headers.Authorization.ToString(), expected, StringComparison.Ordinal);
    }

    private async Task StreamsAsync(HttpContext context)
    {
        HttpRequest request = context.Request;
        string? name = request.Query["name"];
        string? source = request.Query["src"];

        if (HttpMethods.IsGet(request.Method))
        {
            Dictionary<string, object> listing;

            lock (_gate)
            {
                listing = _streams.ToDictionary(
                    stream => stream.Key,
                    stream => (object)new { producers = new[] { new { url = stream.Value } } },
                    StringComparer.Ordinal);
            }

            await context.Response.WriteAsJsonAsync(listing);
            return;
        }

        if (HttpMethods.IsPut(request.Method) && name is not null && source is not null)
        {
            bool refused;
            bool unsaved;

            lock (_gate)
            {
                refused = _refusedSources.Contains(source);
                unsaved = _configurationUnwritable;

                if (!refused)
                {
                    _streams[name] = source;
                }
            }

            // go2rtc's own texts, newline and all: an error of its streams package for a source it
            // will not serve, and the YAML error of patching go2rtc.yaml for a file it cannot save.
            if (refused)
            {
                await AnswerAsync(context, StatusCodes.Status400BadRequest, "streams: source from insecure producer\n");
            }
            else if (unsaved)
            {
                await AnswerAsync(context, StatusCodes.Status400BadRequest, "yaml: line 1: did not find expected key\n");
            }

            return;
        }

        if (HttpMethods.IsDelete(request.Method))
        {
            // Only src removes. name is accepted and answers the same 200, and leaves the stream
            // where it was.
            if (source is not null)
            {
                lock (_gate)
                {
                    _streams.Remove(source);
                }
            }

            return;
        }

        // Not a shape Homespool sends; the status is this fake's.
        await AnswerAsync(context, StatusCodes.Status400BadRequest, "bad request");
    }

    private async Task FrameAsync(HttpContext context)
    {
        switch (CameraFor(context.Request.Query["src"]))
        {
            case null:
                await AnswerAsync(context, StatusCodes.Status404NotFound, StreamNotFound);
                break;

            case { Producing: false }:
                // A 200 with nothing in it and no content type, which go2rtc gives after about five
                // seconds of trying the camera. The wait is left out.
                context.Response.ContentLength = 0;
                break;

            default:
                // With tables: go2rtc's still endpoint repairs a frame that has none.
                context.Response.ContentType = "image/jpeg";
                await context.Response.Body.WriteAsync(FakeCamera.FrameWithTables);
                break;
        }
    }

    private async Task MjpegAsync(HttpContext context)
    {
        FakeCamera? camera = CameraFor(context.Request.Query["src"]);

        if (camera is null)
        {
            await AnswerAsync(context, StatusCodes.Status404NotFound, StreamNotFound);
            return;
        }

        // As frame.jpeg: a 200 with nothing in it, so a success status is not a picture - which is
        // why the relay waits for a whole first part before committing its own answer.
        if (!camera.Producing)
        {
            context.Response.ContentLength = 0;
            return;
        }

        context.Response.ContentType = $"multipart/x-mixed-replace; boundary={MjpegBoundary}";
        await context.Response.StartAsync();

        ReadOnlyMemory<byte> frame = camera.Garbled ? new byte[FakeCamera.Frame.Length] :
                                     camera.TablesInStream ? FakeCamera.FrameWithTables : FakeCamera.Frame;

        Interlocked.Increment(ref _openMjpegStreams);

        CancellationToken cut;

        lock (_gate)
        {
            cut = _cut.Token;
        }

        using CancellationTokenSource watching = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, cut);

        try
        {
            byte[] part = [.. Encoding.ASCII.GetBytes(
                               $"--{MjpegBoundary}\r\nContent-Type: image/jpeg\r\nContent-Length: {frame.Length}\r\n\r\n"),
                           .. frame.Span,
                           .. "\r\n"u8];

            while (!watching.IsCancellationRequested)
            {
                await context.Response.Body.WriteAsync(part, watching.Token);
                await context.Response.Body.FlushAsync(watching.Token);
                await Task.Delay(MjpegFrameInterval, watching.Token);
            }
        }
        catch (Exception exception) when (exception is OperationCanceledException or IOException)
        {
            // The viewer left, or the stream was cut - in which case the connection goes with it,
            // as it does when the process at the other end restarts.
            if (cut.IsCancellationRequested)
            {
                context.Abort();
            }
        }
        finally
        {
            Interlocked.Decrement(ref _openMjpegStreams);
        }
    }

    private async Task WebRtcAsync(HttpContext context)
    {
        FakeCamera? camera = CameraFor(context.Request.Query["src"]);

        if (camera is null || !HttpMethods.IsPost(context.Request.Method))
        {
            await AnswerAsync(context, StatusCodes.Status404NotFound, StreamNotFound);
            return;
        }

        using JsonDocument offer = await JsonDocument.ParseAsync(context.Request.Body);

        lock (_gate)
        {
            _offers.Add(offer.RootElement.GetProperty("sdp").GetString() ?? string.Empty);
        }

        switch (camera.Offer)
        {
            case FakeOfferAnswer.Answer:
                await context.Response.WriteAsJsonAsync(new { type = "answer", sdp = FakeCamera.AnswerSdp });
                break;

            case FakeOfferAnswer.Hang:
                try
                {
                    await Task.Delay(Timeout.Infinite, context.RequestAborted);
                }
                catch (OperationCanceledException)
                {
                    // The asker gave up.
                }

                break;

            case FakeOfferAnswer.CodecsNotMatched:
                // The measured wording. Its 500 is what an unplugged camera answers too, which is why
                // Homespool reads the body.
                await AnswerAsync(context, StatusCodes.Status500InternalServerError,
                                  "streams: codecs not matched: video:H265 => video:H264");
                break;

            default:
                await AnswerAsync(context, StatusCodes.Status500InternalServerError, "streams: timeout");
                break;
        }
    }

    private async Task ConfigAsync(HttpContext context)
    {
        if (HttpMethods.IsGet(context.Request.Method))
        {
            await AnswerAsync(context, StatusCodes.Status200OK, Config);
            return;
        }

        if (HttpMethods.IsPatch(context.Request.Method))
        {
            using StreamReader reader = new(context.Request.Body, Encoding.UTF8);
            string document = await reader.ReadToEndAsync(context.RequestAborted);

            // Merged into the document, and the streams survive it. The fake keeps the written
            // document as its text rather than merging, because the one thing Homespool asks of the
            // configuration is whether a candidate is in it.
            lock (_gate)
            {
                _config = document;
                _configWrites.Add(document);
            }

            return;
        }

        await AnswerAsync(context, StatusCodes.Status400BadRequest, "bad request");
    }

    private async Task RestartAsync(HttpContext context)
    {
        // POST is the method that works: GET answers 400.
        if (!HttpMethods.IsPost(context.Request.Method))
        {
            await AnswerAsync(context, StatusCodes.Status400BadRequest, "bad request");
            return;
        }

        Interlocked.Increment(ref _restarts);
    }

    /// <summary>The camera behind a stream name, or null when the sidecar holds no such stream.</summary>
    private FakeCamera? CameraFor(string? streamName)
    {
        lock (_gate)
        {
            if (streamName is null || !_streams.TryGetValue(streamName, out string? source))
            {
                return null;
            }

            return _cameras.GetValueOrDefault(source, FakeCamera.Absent);
        }
    }

    private static async Task AnswerAsync(HttpContext context, int status, string body)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "text/plain; charset=utf-8";
        await context.Response.WriteAsync(body);
    }

    private async Task AcceptRtspAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient connection;

            try
            {
                connection = await _rtsp.AcceptTcpClientAsync(cancellationToken);
            }
            catch (Exception exception) when (exception is SocketException or ObjectDisposedException)
            {
                return;
            }

            _ = AnswerRtspAsync(connection, cancellationToken);
        }
    }

    /// <summary>
    /// Answers one <c>DESCRIBE</c> with the camera's codecs as an SDP, the way go2rtc builds one by
    /// connecting the camera - or refuses, for a stream it does not hold or a camera producing nothing.
    /// </summary>
    private async Task AnswerRtspAsync(TcpClient connection, CancellationToken cancellationToken)
    {
        using (connection)
        {
            try
            {
                NetworkStream wire = connection.GetStream();
                string request = await ReadRtspRequestAsync(wire, cancellationToken);

                // DESCRIBE rtsp://host:port/<name> RTSP/1.0
                string[] requestLine = request.Split("\r\n")[0].Split(' ');
                string name = requestLine.Length == 3 ? requestLine[1][(requestLine[1].LastIndexOf('/') + 1)..] : string.Empty;

                Interlocked.Increment(ref _describes);

                FakeCamera? camera = CameraFor(name);
                string answer;

                if (requestLine[0] == "DESCRIBE" && camera is { Producing: true })
                {
                    string sdp = camera.Sdp();
                    answer = "RTSP/1.0 200 OK\r\nCSeq: 1\r\nContent-Type: application/sdp\r\n" +
                             $"Content-Length: {Encoding.ASCII.GetByteCount(sdp)}\r\n\r\n{sdp}";
                }
                else
                {
                    // Homespool reads anything but 200 as "no answer today"; the status is this fake's.
                    answer = "RTSP/1.0 404 Not Found\r\nCSeq: 1\r\n\r\n";
                }

                await wire.WriteAsync(Encoding.ASCII.GetBytes(answer), cancellationToken);
            }
            catch (Exception exception) when (exception is IOException or OperationCanceledException)
            {
                // The prober gave up, or the fake is stopping.
            }
        }
    }

    private static async Task<string> ReadRtspRequestAsync(NetworkStream wire, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[8 * 1024];
        int length = 0;

        while (length < buffer.Length)
        {
            int read = await wire.ReadAsync(buffer.AsMemory(length), cancellationToken);

            if (read == 0)
            {
                break;
            }

            length += read;

            if (Encoding.ASCII.GetString(buffer, 0, length).Contains("\r\n\r\n", StringComparison.Ordinal))
            {
                break;
            }
        }

        return Encoding.ASCII.GetString(buffer, 0, length);
    }
}

/// <summary>What a <see cref="FakeCamera"/> does with a WebRTC offer.</summary>
public enum FakeOfferAnswer
{
    Undefined = 0,

    /// <summary>Answers it.</summary>
    Answer = 1,

    /// <summary>Refuses it as go2rtc words a codec mismatch.</summary>
    CodecsNotMatched = 2,

    /// <summary>Fails for some other reason.</summary>
    Fail = 3,

    /// <summary>
    /// Never answers, until the asker gives up - a sidecar wedged on the camera, which is the case a
    /// viewer's own deadline exists for.
    /// </summary>
    Hang = 4,
}

/// <summary>
/// A camera behind the <see cref="FakeGo2Rtc"/>: what it is encoded as, whether it is producing
/// pictures, what the sidecar does with a WebRTC offer for it, and whether its MJPEG stream reaches
/// Homespool with Huffman tables in every frame.
/// </summary>
/// <param name="Codecs">The video codecs an RTSP <c>DESCRIBE</c> names.</param>
/// <param name="Producing">Whether it sends pictures at all.</param>
/// <param name="Offer">What the sidecar does with a WebRTC offer for it.</param>
/// <param name="TablesInStream">
/// True for Homespool's own sidecar image, which adds the tables a USB camera's frames leave out.
/// False for a sidecar that passes such frames on as they came - upstream go2rtc, or ours without
/// its patch - which Homespool relays untouched too, so Safari shows nothing.
/// </param>
/// <param name="Garbled">
/// Whether its MJPEG stream carries parts no browser can decode - zeros where the picture should be.
/// A stream is then open and busy and shows nothing, which is the case a page must not call live.
/// </param>
public sealed record FakeCamera(IReadOnlySet<string> Codecs, bool Producing, FakeOfferAnswer Offer, bool TablesInStream = true, bool Garbled = false)
{
    /// <summary>
    /// A USB camera's usual shape: Motion-JPEG, producing - and never watchable over WebRTC, which
    /// carries no JPEG, so an offer for it is refused on codecs.
    /// </summary>
    public static readonly FakeCamera Jpeg = new(new HashSet<string>(StringComparer.Ordinal) { "JPEG" }, true, FakeOfferAnswer.CodecsNotMatched);

    /// <summary>A network camera's usual shape: H.264, producing, and watchable over WebRTC.</summary>
    public static readonly FakeCamera H264 = new(new HashSet<string>(StringComparer.Ordinal) { "H264" }, true, FakeOfferAnswer.Answer);

    /// <summary>A camera that is not there: switched off, unplugged, or an address nothing answers.</summary>
    public static readonly FakeCamera Absent = new(new HashSet<string>(StringComparer.Ordinal), false, FakeOfferAnswer.Fail);

    /// <summary>The session description every answered offer carries.</summary>
    public const string AnswerSdp = "v=0\r\no=- 0 0 IN IP4 127.0.0.1\r\ns=fake-go2rtc\r\nt=0 0\r\n";

    /// <summary>
    /// One frame as a USB camera sends it over MJPEG: a real 32x24 picture with its Huffman tables
    /// left out, the AVI1 convention - so a relayed stream shows whether anything between the sidecar
    /// and the viewer changed it, and a browser shows whether what it was given decodes.
    /// </summary>
    /// <remarks>
    /// <see cref="FrameWithTables"/> with its four DHT segments removed and nothing else changed. The
    /// encoder's tables are the standard ones of JPEG Annex K, byte for byte the block go2rtc puts back
    /// into a still, so a repaired frame is the original picture again.
    /// </remarks>
    public static ReadOnlyMemory<byte> Frame { get; } = Convert.FromBase64String(
        "/9j/4AAQSkZJRgABAQAAAQABAAD/2wBDAAoHBwgHBgoICAgLCgoLDhgQDg0NDh0VFhEYIx8lJCIfIiEmKzcvJik0KSEiMEEx" +
        "NDk7Pj4+JS5ESUM8SDc9Pjv/2wBDAQoLCw4NDhwQEBw7KCIoOzs7Ozs7Ozs7Ozs7Ozs7Ozs7Ozs7Ozs7Ozs7Ozs7Ozs7Ozs7" +
        "Ozs7Ozs7Ozs7Ozs7Ozv/wAARCAAYACADASIAAhEBAxEB/9oADAMBAAIRAxEAPwDzOGw9q2IbD2rWhsPap4bD2rapi/qnnf5b" +
        "GGCx3NbUyYbD2rXhsPateGw9qnhsPavNqYv6p53+Wx9pgsdzW1K8Nh7Vrw2HtRRXm4WpL3vl+p+PYStPQnhsPateGw9qKK87" +
        "C1Je98v1Ps8JWnof/9k=");

    /// <summary>
    /// The same picture with its Huffman tables in place, as <c>frame.jpeg</c> serves it and as
    /// Homespool's own sidecar image sends it on <c>stream.mjpeg</c>. Encoded by libjpeg with its
    /// defaults, which are the standard tables.
    /// </summary>
    public static ReadOnlyMemory<byte> FrameWithTables { get; } = Convert.FromBase64String(
        "/9j/4AAQSkZJRgABAQAAAQABAAD/2wBDAAoHBwgHBgoICAgLCgoLDhgQDg0NDh0VFhEYIx8lJCIfIiEmKzcvJik0KSEiMEEx" +
        "NDk7Pj4+JS5ESUM8SDc9Pjv/2wBDAQoLCw4NDhwQEBw7KCIoOzs7Ozs7Ozs7Ozs7Ozs7Ozs7Ozs7Ozs7Ozs7Ozs7Ozs7Ozs7" +
        "Ozs7Ozs7Ozs7Ozs7Ozv/wAARCAAYACADASIAAhEBAxEB/8QAHwAAAQUBAQEBAQEAAAAAAAAAAAECAwQFBgcICQoL/8QAtRAA" +
        "AgEDAwIEAwUFBAQAAAF9AQIDAAQRBRIhMUEGE1FhByJxFDKBkaEII0KxwRVS0fAkM2JyggkKFhcYGRolJicoKSo0NTY3ODk6" +
        "Q0RFRkdISUpTVFVWV1hZWmNkZWZnaGlqc3R1dnd4eXqDhIWGh4iJipKTlJWWl5iZmqKjpKWmp6ipqrKztLW2t7i5usLDxMXG" +
        "x8jJytLT1NXW19jZ2uHi4+Tl5ufo6erx8vP09fb3+Pn6/8QAHwEAAwEBAQEBAQEBAQAAAAAAAAECAwQFBgcICQoL/8QAtREA" +
        "AgECBAQDBAcFBAQAAQJ3AAECAxEEBSExBhJBUQdhcRMiMoEIFEKRobHBCSMzUvAVYnLRChYkNOEl8RcYGRomJygpKjU2Nzg5" +
        "OkNERUZHSElKU1RVVldYWVpjZGVmZ2hpanN0dXZ3eHl6goOEhYaHiImKkpOUlZaXmJmaoqOkpaanqKmqsrO0tba3uLm6wsPE" +
        "xcbHyMnK0tPU1dbX2Nna4uPk5ebn6Onq8vP09fb3+Pn6/9oADAMBAAIRAxEAPwDzOGw9q2IbD2rWhsPap4bD2rapi/qnnf5b" +
        "GGCx3NbUyYbD2rXhsPateGw9qnhsPavNqYv6p53+Wx9pgsdzW1K8Nh7Vrw2HtRRXm4WpL3vl+p+PYStPQnhsPateGw9qKK87" +
        "C1Je98v1Ps8JWnof/9k=");

    /// <summary>An SDP naming this camera's codecs in one video section, as a DESCRIBE answer carries.</summary>
    public string Sdp()
    {
        StringBuilder sdp = new("v=0\r\no=- 0 0 IN IP4 127.0.0.1\r\ns=fake-go2rtc\r\nt=0 0\r\n");
        List<string> codecs = [.. Codecs.Order(StringComparer.Ordinal)];

        sdp.Append(System.Globalization.CultureInfo.InvariantCulture,
                   $"m=video 0 RTP/AVP {string.Join(' ', codecs.Select((_, index) => 96 + index))}\r\n");

        for (int index = 0; index < codecs.Count; index++)
        {
            sdp.Append(System.Globalization.CultureInfo.InvariantCulture, $"a=rtpmap:{96 + index} {codecs[index]}/90000\r\n");
        }

        return sdp.ToString();
    }
}
