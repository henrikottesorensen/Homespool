using System;
using System.IO;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;

namespace Homespool.Host.IntegrationTest;

/// <summary>
/// A fact that skips itself unless a Mailpit offering STARTTLS is actually listening.
/// </summary>
/// <remarks>
/// <para>
/// <b>Skipped, not failed.</b> The fixture is a throwaway CA and a Mailpit container, made by a script
/// somebody has to run - so a fresh clone that types <c>dotnet test</c> would otherwise meet a red
/// suite reporting something that has nothing to do with whatever they were about to change. Red
/// should mean the code is wrong.
/// </para>
/// <para>
/// <b>Both halves are checked, and the second one is why this is not just a file test.</b> The CA file
/// proves the fixture was generated at some point; it says nothing about what is listening now. A
/// plain Mailpit is the obvious thing to run as a development inbox - the application cannot send to
/// the TLS one, whose certificate names only <c>localhost</c> and is signed by an authority nothing
/// trusts - and with a CA file left over from an earlier run, this test then met a server that cannot
/// negotiate STARTTLS and failed. A red suite that reports the wrong thing teaches people to ignore
/// red.
/// </para>
/// <para>
/// <b>It is not a licence to leave the test unrun.</b> CI runs <c>start-mailpit-tls.sh</c> first, so
/// there the real handshake is exercised on every push - which matters, because this is the only test
/// anywhere that puts a genuine STARTTLS negotiation in front of <c>SmtpEmailSender</c>, and
/// <c>housekeeping.md</c> records that alert mail had never actually been sent.
/// </para>
/// <para>
/// The check runs at discovery, which is why this is an attribute rather than a call inside the test:
/// xunit 2.9's <c>Assert.Skip</c> is not available in the assert package this project references.
/// </para>
/// </remarks>
public sealed class RequiresMailpitTlsFixtureFactAttribute : FactAttribute
{
    /// <summary>The Mailpit the fixture script starts, and the one the tests are written against.</summary>
    private const string FixtureHost = "localhost";

    /// <summary>Mailpit's SMTP port. 1025 rather than 25, which needs privileges nobody should need.</summary>
    private const int FixturePort = 1025;

    /// <summary>
    /// Short on purpose: this runs at discovery, for every test in the class, before anything useful
    /// has happened. A fixture that is up answers immediately; one that is not should not be waited on.
    /// </summary>
    private const int ProbeTimeoutMilliseconds = 1500;

    /// <summary>Skips the test at discovery unless a STARTTLS-capable Mailpit is reachable.</summary>
    /// <param name="sourceFilePath">
    /// Where the test is declared, which locates the fixture directory beside it - and, since v3,
    /// what the runner reports as the test's source location.
    /// </param>
    /// <param name="sourceLineNumber">
    /// Required by xUnit3003: a custom <see cref="FactAttribute"/> must be able to tell v3's
    /// in-process runner where it came from, or the test has no source information at all.
    /// </param>
    public RequiresMailpitTlsFixtureFactAttribute([CallerFilePath] string sourceFilePath = "",
                                                  [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        string caCertPath = Path.Combine(Path.GetDirectoryName(sourceFilePath)!, ".mailpit-tls", "ca-cert.pem");

        if (!File.Exists(caCertPath))
        {
            Skip = "No local Mailpit TLS fixture. Run Homespool.Host.IntegrationTest/start-mailpit-tls.sh, "
                 + "which brings up Mailpit with STARTTLS and generates the throwaway CA this verifies against.";
            return;
        }

        if (!AdvertisesStartTls())
        {
            Skip = $"Nothing on {FixtureHost}:{FixturePort} advertises STARTTLS, though the throwaway CA from "
                 + "an earlier run is still here. A plain Mailpit is probably running as a development "
                 + "inbox. Run Homespool.Host.IntegrationTest/start-mailpit-tls.sh to exercise the real "
                 + "handshake.";
        }
    }

    /// <summary>
    /// Whether the server on the fixture port offers STARTTLS, asked by having the conversation.
    /// </summary>
    /// <remarks>
    /// A plain socket and the two lines of SMTP that settle it, rather than a mail library: this needs
    /// to answer "would the real client get the chance to upgrade" and then get out, without
    /// authenticating, sending, or caring what else the server supports. Anything unexpected - refused,
    /// slow, not SMTP at all - answers no, because a fixture that cannot be talked to is a fixture that
    /// is not there.
    /// </remarks>
    private static bool AdvertisesStartTls()
    {
        try
        {
            // Connected synchronously rather than with a timeout wrapped round ConnectAsync: this is
            // a constructor, so it cannot await, and waiting on the task trips VSTHRD002 for a good
            // reason. The fixture is always on loopback, where the two outcomes that matter - listening
            // and refused - both answer at once. The read timeouts below cover the rest.
            using TcpClient client = new();
            client.Connect(FixtureHost, FixturePort);

            client.ReceiveTimeout = ProbeTimeoutMilliseconds;
            client.SendTimeout = ProbeTimeoutMilliseconds;

            using NetworkStream stream = client.GetStream();
            using StreamReader reader = new(stream, Encoding.ASCII, false, 1024, leaveOpen: true);

            // CRLF, explicitly. WriteLine would use Environment.NewLine - a bare LF on macOS and Linux -
            // and SMTP requires CRLF, so the server never sees a complete command: it waits, the read
            // times out, and the probe reports no fixture while a perfectly good one is listening.
            using StreamWriter writer = new(stream, Encoding.ASCII, 1024, leaveOpen: true)
            {
                AutoFlush = true,
                NewLine = "\r\n",
            };

            // The greeting first, or EHLO is written into a conversation that has not started.
            if (reader.ReadLine() is not { } greeting || !greeting.StartsWith("220", StringComparison.Ordinal))
            {
                return false;
            }

            writer.WriteLine("EHLO homespool-test");

            // Extensions come back as "250-NAME" with "250 NAME" on the last one, so the loop ends on
            // the space rather than on running out of lines.
            while (reader.ReadLine() is { } line)
            {
                if (line.Contains("STARTTLS", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                if (line.Length < 4 || line[3] != '-')
                {
                    return false;
                }
            }

            return false;
        }
        catch (Exception)
        {
            // SocketException on a refused connection, IOException on a timed-out read, and anything
            // else a server that is not SMTP might provoke. Every one means the same thing to a
            // caller: there is no fixture to test against.
            return false;
        }
    }
}
