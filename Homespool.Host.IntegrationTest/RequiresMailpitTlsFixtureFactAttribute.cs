using System.IO;
using System.Runtime.CompilerServices;

using Xunit;
using Xunit.Sdk;

namespace Homespool.Host.IntegrationTest;

/// <summary>
/// A fact that skips itself when the local Mailpit TLS fixture has not been created.
/// </summary>
/// <remarks>
/// <para>
/// <b>Skipped, not failed.</b> The fixture is a throwaway CA and a Mailpit container, made by a script
/// somebody has to run - so a fresh clone that types <c>dotnet test</c> would otherwise meet a red
/// suite reporting something that has nothing to do with whatever they were about to change. Red
/// should mean the code is wrong.
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
    /// <summary>Skips the test at discovery when the local Mailpit TLS fixture is absent.</summary>
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
        }
    }
}
