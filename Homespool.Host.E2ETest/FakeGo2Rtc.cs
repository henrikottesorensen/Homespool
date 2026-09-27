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
/// answers 200 and removes nothing, only <c>?src=</c> removes; a configuration write replaces the
/// document and takes every registered stream with it; a WebRTC offer the camera's codecs cannot
/// meet is refused by a body saying <c>codecs not matched</c>, not by its status; and
/// <c>stream.mjpeg</c> answers 200 before it knows whether the camera will produce anything.
/// </para>
/// <para>
/// <b>A fake encodes a belief about go2rtc, and a belief can be wrong</b> - an empty
/// <c>streams: {}</c> refusing every stream was one. That is what a test against the pinned image
/// is for; this is what lets the code on Homespool's side of the API be exercised at all.
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
                // Homespool reads only whether this succeeded; the status is this fake's.
                await AnswerAsync(context, StatusCodes.Status500InternalServerError, "streams: timeout");
                break;

            default:
                context.Response.ContentType = "image/jpeg";
                await context.Response.Body.WriteAsync(FakeCamera.Frame);
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

        // 200 before anything is known about the camera, as go2rtc does - which is why the relay
        // waits for a whole first part before committing its own answer.
        context.Response.ContentType = $"multipart/x-mixed-replace; boundary={MjpegBoundary}";
        await context.Response.StartAsync();

        if (!camera.Producing)
        {
            return;
        }

        Interlocked.Increment(ref _openMjpegStreams);

        try
        {
            byte[] part = [.. Encoding.ASCII.GetBytes(
                               $"--{MjpegBoundary}\r\nContent-Type: image/jpeg\r\nContent-Length: {FakeCamera.Frame.Length}\r\n\r\n"),
                           .. FakeCamera.Frame.Span,
                           .. "\r\n"u8];

            while (!context.RequestAborted.IsCancellationRequested)
            {
                await context.Response.Body.WriteAsync(part, context.RequestAborted);
                await context.Response.Body.FlushAsync(context.RequestAborted);
                await Task.Delay(MjpegFrameInterval, context.RequestAborted);
            }
        }
        catch (Exception exception) when (exception is OperationCanceledException or IOException)
        {
            // The viewer left.
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

            // Replaces rather than merges, and the streams go with it. On the real sidecar they
            // linger in memory until the next restart; here they go at once, which is the state
            // that restart leaves.
            lock (_gate)
            {
                _config = document;
                _configWrites.Add(document);
                _streams.Clear();
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
}

/// <summary>
/// A camera behind the <see cref="FakeGo2Rtc"/>: what it is encoded as, whether it is producing
/// pictures, and what the sidecar does with a WebRTC offer for it.
/// </summary>
public sealed record FakeCamera(IReadOnlySet<string> Codecs, bool Producing, FakeOfferAnswer Offer)
{
    /// <summary>A USB camera's usual shape: Motion-JPEG, producing.</summary>
    public static readonly FakeCamera Jpeg = new(new HashSet<string>(StringComparer.Ordinal) { "JPEG" }, true, FakeOfferAnswer.Answer);

    /// <summary>A network camera's usual shape: H.264, producing, and watchable over WebRTC.</summary>
    public static readonly FakeCamera H264 = new(new HashSet<string>(StringComparer.Ordinal) { "H264" }, true, FakeOfferAnswer.Answer);

    /// <summary>A camera that is not there: switched off, unplugged, or an address nothing answers.</summary>
    public static readonly FakeCamera Absent = new(new HashSet<string>(StringComparer.Ordinal), false, FakeOfferAnswer.Fail);

    /// <summary>The session description every answered offer carries.</summary>
    public const string AnswerSdp = "v=0\r\no=- 0 0 IN IP4 127.0.0.1\r\ns=fake-go2rtc\r\nt=0 0\r\n";

    /// <summary>
    /// One frame in the AVI1 shape a USB camera sends: SOI, APP0, SOF, SOS, data, EOI, and no Huffman
    /// tables - so a relayed stream shows whether anything between the sidecar and the viewer changed it.
    /// </summary>
    public static ReadOnlyMemory<byte> Frame { get; } = new byte[]
    {
        0xFF, 0xD8,
        0xFF, 0xE0, 0x00, 0x08, (byte)'A', (byte)'V', (byte)'I', (byte)'1', 0x00, 0x00,
        0xFF, 0xC0, 0x00, 0x05, 0x08, 0x00, 0x01,
        0xFF, 0xDA, 0x00, 0x04, 0x01, 0x02,
        0x11, 0x22, 0x33,
        0xFF, 0xD9,
    };

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
