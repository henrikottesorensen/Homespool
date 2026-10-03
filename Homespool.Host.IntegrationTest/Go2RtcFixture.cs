using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;

namespace Homespool.Host.IntegrationTest;

/// <summary>
/// Where the camera sidecar <c>start-go2rtc.sh</c> starts is found, and the credential it was given.
/// The values must match the script's.
/// </summary>
/// <remarks>
/// <para>
/// Neither port is go2rtc's default, so a sidecar a developer happens to be running for their own
/// stack cannot answer in this one's place.
/// </para>
/// <para>
/// The credential carries a quote, a backslash, <c>: </c> and <c> #</c>, none of which a quoted YAML
/// or JSON string holds as written, so every test here is also the test that such a credential
/// reaches the sidecar unchanged by the road <c>compose.yaml</c> gives it.
/// </para>
/// </remarks>
internal static class Go2RtcFixture
{
    public const string Username = "contract\"user\\";
    public const string Password = "contract \"sidecar\" \\password: #1"; // betterleaks:allow - the credential of a sidecar that lives only for a test run

    public const int RtspPort = 18554;

    public static readonly Uri BaseAddress = new("http://127.0.0.1:11984");

    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(2);

    private static readonly Lazy<bool> Serving = new(Probe);

    /// <summary>Whether the sidecar answers an authenticated request, asked once per test run.</summary>
    public static bool IsServing => Serving.Value;

    /// <summary>Why a test against the real sidecar was skipped, and what would run it.</summary>
    public const string NotServing =
        "No camera sidecar answers on 127.0.0.1:11984 with the contract credential. Run " +
        "Homespool.Host.IntegrationTest/start-go2rtc.sh, which starts Homespool's go2rtc image the way " +
        "compose.yaml runs it.";

    private static bool Probe()
    {
        try
        {
            using HttpClient client = new() { Timeout = ProbeTimeout };
            using HttpRequestMessage request = new(HttpMethod.Get, new Uri(BaseAddress, "/api/streams"));
            request.Headers.Authorization = new AuthenticationHeaderValue(
                "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{Username}:{Password}")));

            using HttpResponseMessage response = client.Send(request);

            return response.IsSuccessStatusCode;
        }
        catch (Exception)
        {
            // A refused connection, the timeout, or a half-started container: every one means there
            // is no sidecar to test against.
            return false;
        }
    }
}
