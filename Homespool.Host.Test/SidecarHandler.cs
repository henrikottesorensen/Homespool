using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Web;

namespace Homespool.Host.Test;

/// <summary>
/// The sidecar's stream API as a handler: it holds streams by name, answers the listing and the
/// configuration file from them, and records what it was asked.
/// </summary>
/// <remarks>
/// <para>
/// The file is written with each source double-quoted, as JSON escapes it - valid YAML, and not
/// go2rtc's own spelling, which <c>Go2RtcStreamSourcesTests</c> holds the parser to separately.
/// </para>
/// <para>
/// Anything that is not the listing, the file, a registration or a removal answers 200 with an empty
/// object, since the probe that follows a registration is not what these tests are about.
/// </para>
/// </remarks>
internal sealed class SidecarHandler : HttpMessageHandler
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, string> _streams = new(StringComparer.Ordinal);

    private int _listings;

    /// <summary>A sidecar holding <paramref name="held"/>, or nothing.</summary>
    /// <param name="held">The streams the sidecar holds before anything is asked, by name.</param>
    public SidecarHandler(IReadOnlyDictionary<string, string>? held = null)
    {
        foreach ((string name, string source) in held ?? new Dictionary<string, string>())
        {
            _streams[name] = source;
        }
    }

    /// <summary>Listings refused before one is answered, the way a restarting sidecar's closed port refuses them.</summary>
    public int ListingsToRefuse { get; init; }

    /// <summary>
    /// Registrations answered with go2rtc's refusal of a source before one is accepted, the way a
    /// sidecar that has just come back refuses every source.
    /// </summary>
    public int RegistrationsToRefuse { get; init; }

    /// <summary>Registrations whose connection is refused before one is answered at all.</summary>
    public int RegistrationsToDrop { get; init; }

    /// <summary>
    /// Called as each listing arrives, before it is answered or refused, with how many came before it -
    /// for a test that changes a camera while something is waiting on the sidecar.
    /// </summary>
    public Func<int, Task>? OnListing { get; init; }

    /// <summary>
    /// Called as each registration arrives, before it is answered, with how many came before it and the
    /// source it carries.
    /// </summary>
    public Func<int, string, Task>? OnRegistration { get; init; }

    /// <summary>
    /// Streams kept in the file but left out of the listing, the way a removal whose file write failed
    /// leaves one: gone from the running sidecar, still in go2rtc.yaml.
    /// </summary>
    public HashSet<string> NotRunning { get; } = new(StringComparer.Ordinal);

    /// <summary>The clock <see cref="AttemptedAt"/> is read from.</summary>
    public TimeProvider Clock { get; set; } = TimeProvider.System;

    /// <summary>How many listings were asked for, answered or not.</summary>
    public int Listings => Volatile.Read(ref _listings);

    /// <summary>Every stream name a removal was sent for.</summary>
    public List<string> Deleted { get; } = [];

    /// <summary>Every stream name a registration was sent for, answered or not.</summary>
    public List<Guid> Attempted { get; } = [];

    /// <summary>The source each of <see cref="Attempted"/> carried.</summary>
    public List<string> AttemptedSources { get; } = [];

    /// <summary>When each of <see cref="Attempted"/> was made.</summary>
    public List<DateTimeOffset> AttemptedAt { get; } = [];

    /// <summary>The stream names whose registration was accepted.</summary>
    public List<Guid> Registered { get; } = [];

    /// <summary>The streams the sidecar holds now, by name.</summary>
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

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
                                                                 CancellationToken cancellationToken)
    {
        string path = request.RequestUri!.AbsolutePath;
        System.Collections.Specialized.NameValueCollection query = HttpUtility.ParseQueryString(request.RequestUri.Query);

        if (request.Method == HttpMethod.Get && path == "/api/streams")
        {
            int before = Interlocked.Increment(ref _listings) - 1;

            if (OnListing is not null)
            {
                await OnListing(before);
            }

            if (before < ListingsToRefuse)
            {
                throw new HttpRequestException("Connection refused");
            }

            lock (_gate)
            {
                return Answer(JsonSerializer.Serialize(_streams.Where(stream => !NotRunning.Contains(stream.Key))
                                                               .ToDictionary(stream => stream.Key, _ => new { })));
            }
        }

        if (request.Method == HttpMethod.Get && path == "/api/config")
        {
            return Answer(ConfigFile());
        }

        if (request.Method == HttpMethod.Delete && query["src"] is { } deleted)
        {
            lock (_gate)
            {
                Deleted.Add(deleted);
                _streams.Remove(deleted);
            }

            return Answer(string.Empty);
        }

        if (request.Method == HttpMethod.Put &&
            path == "/api/streams" &&
            query["name"] is { } name &&
            Guid.TryParse(name, out Guid uuid) &&
            query["src"] is { } source)
        {
            int before;

            lock (_gate)
            {
                before = Attempted.Count;
                Attempted.Add(uuid);
                AttemptedSources.Add(source);
                AttemptedAt.Add(Clock.GetUtcNow());
            }

            if (OnRegistration is not null)
            {
                await OnRegistration(before, source);
            }

            if (before < RegistrationsToDrop)
            {
                throw new HttpRequestException("Connection refused");
            }

            if (before < RegistrationsToDrop + (long)RegistrationsToRefuse)
            {
                return new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    Content = new StringContent("streams: source not supported\n"),
                };
            }

            lock (_gate)
            {
                Registered.Add(uuid);
                _streams[name] = source;
                NotRunning.Remove(name);
            }

            return Answer(string.Empty);
        }

        return Answer("{}");
    }

    private string ConfigFile()
    {
        lock (_gate)
        {
            if (_streams.Count == 0)
            {
                return string.Empty;
            }

            StringBuilder file = new("streams:\n");

            foreach ((string name, string source) in _streams)
            {
                file.Append("  ").Append(JsonSerializer.Serialize(name)).Append(":\n");
                file.Append("    - ").Append(JsonSerializer.Serialize(source)).Append('\n');
            }

            return file.ToString();
        }
    }

    private static HttpResponseMessage Answer(string body)
    {
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
    }
}
