using System;
using System.Net.Http;
using System.Runtime.CompilerServices;

namespace Homespool.Host.IntegrationTest;

/// <summary>
/// A fact that skips itself unless the dex fixture is actually serving discovery.
/// </summary>
/// <remarks>
/// <para>
/// <b>Skipped, not failed</b> — the same rule as <see cref="RequiresMailpitTlsFixtureFactAttribute"/>,
/// and for the same reason: the fixture is a container somebody has to start, so a fresh clone typing
/// <c>dotnet test</c> would otherwise meet red that has nothing to do with whatever they were about to
/// change. Red should mean the code is wrong.
/// </para>
/// <para>
/// <b>Discovery rather than the port</b>, because dex binds its listener before it has finished
/// loading storage and connectors. A connect-only probe therefore answers yes during a window in which
/// every request still fails — which would show up as an occasional failure in whichever test ran
/// first, on a cold runner, and nowhere else. <c>start-dex.sh</c> waits on the same endpoint for the
/// same reason.
/// </para>
/// <para>
/// <b>It is not a licence to leave these unrun.</b> CI runs <c>start-dex.sh</c> before the suite, so
/// there the real authorisation-code flow is exercised on every push — and it is the only thing
/// anywhere that puts a genuine OpenID Connect provider in front of this handler.
/// </para>
/// </remarks>
public sealed class RequiresDexFactAttribute : FactAttribute
{
    /// <summary>
    /// Short on purpose: this runs at discovery, for every test in the class, before anything useful
    /// has happened. A fixture that is up answers immediately; one that is not should not be waited on.
    /// </summary>
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(2);

    /// <summary>Skips the test at discovery unless dex answers.</summary>
    /// <param name="sourceFilePath">
    /// Where the test is declared, which is what v3's runner reports as its source location.
    /// </param>
    /// <param name="sourceLineNumber">
    /// Required by xUnit3003: a custom <see cref="FactAttribute"/> must be able to tell v3's
    /// in-process runner where it came from, or the test has no source information at all.
    /// </param>
    public RequiresDexFactAttribute([CallerFilePath] string sourceFilePath = "",
                                    [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        if (!ServesDiscovery())
        {
            Skip = "No dex fixture is serving OpenID Connect discovery on " + DexFixture.Authority + ". Run "
                   + "Homespool.Host.IntegrationTest/start-dex.sh, which brings up the throwaway "
                   + "provider these tests drive a real authorisation-code flow against.";
        }
    }

    /// <summary>
    /// Whether dex answers its discovery document, asked by fetching it.
    /// </summary>
    /// <remarks>
    /// Synchronously, because this is a constructor and cannot await — and waiting on a task here trips
    /// VSTHRD002 for a good reason. The fixture is always on loopback, where the outcomes that matter
    /// answer at once; <see cref="ProbeTimeout"/> covers a container that is up but not yet serving.
    /// </remarks>
    private static bool ServesDiscovery()
    {
        try
        {
            using HttpClient client = new() { Timeout = ProbeTimeout };
            using HttpRequestMessage request = new(HttpMethod.Get, DexFixture.DiscoveryUrl);
            using HttpResponseMessage response = client.Send(request);

            return response.IsSuccessStatusCode;
        }
        catch (Exception)
        {
            // HttpRequestException on a refused connection, TaskCanceledException on the timeout, and
            // anything else a half-started container might provoke. Every one means the same thing to
            // a caller: there is no fixture to test against.
            return false;
        }
    }
}
