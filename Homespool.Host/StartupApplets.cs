using System;

using Homespool.Data;
using Homespool.Host.Configuration;

namespace Homespool.Host;

/// <summary>
/// The arguments that answer a question and exit, rather than starting a server.
/// </summary>
/// <remarks>
/// <para>
/// <b>Answered before the logger, before configuration, before anything.</b> Each of these is asked
/// precisely when something is wrong - no database mounted, a broken <c>.env</c>, an application that
/// will not start - so none of them may need the application to start. <c>--version</c> is also the
/// answer the AGPL's offer of source needs, so it has to hold on an image nobody can log in to.
/// </para>
/// <para>
/// <b>The trap they all share is what an OLDER image does</b>, which <c>setup-env.sh</c> documents at
/// length: an image that predates one of these arguments does not recognise it, hands it to
/// <c>WebApplication.CreateBuilder</c>, STARTS THE SERVER and prints a page of JSON. Anything that
/// ever scripts one of these must bound it in time and check that the output looks like an answer,
/// rather than trusting it.
/// </para>
/// </remarks>
public static class StartupApplets
{
    /// <summary>The argument that turns this into a one-shot time zone conversion rather than a server.</summary>
    /// <remarks>
    /// <para>
    /// <b>Why the server carries this at all.</b> On Windows the wizard runs inside this image, and the
    /// zone Windows reports - <c>W. Europe Standard Time</c> - is not an IANA name, which is what
    /// <c>TZ</c> takes. The conversion is one framework call, and this container is the only .NET in
    /// reach: Windows PowerShell 5.1 is .NET Framework 4.8, where
    /// <c>TryConvertWindowsIdToIanaId</c> does not exist, and a Windows machine that cannot install
    /// Docker Desktop has no other runtime either.
    /// </para>
    /// <para>
    /// The alternative was shipping a mapping table in a shell script, which would be wrong the first
    /// time a zone changed and would have to be maintained by hand against data ICU already has.
    /// </para>
    /// </remarks>
    private const string IanaTimeZoneArgument = "--iana-timezone";

    /// <summary>
    /// Runs whichever applet the first argument names, if it names one at all.
    /// </summary>
    /// <param name="args">The process arguments, exactly as <c>Main</c> received them.</param>
    /// <param name="exitCode">What the process should exit with; meaningless unless this returns true.</param>
    /// <returns>True when an applet answered and no server should be started.</returns>
    public static bool TryRun(string[] args, out int exitCode)
    {
        ArgumentNullException.ThrowIfNull(args);

        exitCode = 0;

        if (args.Length == 0)
        {
            return false;
        }

        switch (args[0])
        {
            // setup-env.sh asks this to turn a Windows time zone into an IANA one and exits.
            case IanaTimeZoneArgument:
                exitCode = WriteIanaTimeZone(args);

                return true;

            // Which build this is - the question asked precisely when "what is actually running?"
            // cannot be answered any other way.
            case BuildInformation.VersionArgument:
                exitCode = BuildInformation.WriteVersion("Homespool");

                return true;

            // The schema this build expects, written to a file so a deployed database can be compared
            // against it. See Homespool.Data.SchemaWriter.
            case SchemaWriter.Argument:
                exitCode = SchemaWriter.Write(args.Length > 1 ? args[1] : null);

                return true;

            // The editable settings this deployment currently carries in its environment, written to
            // the file that now owns them. A one-shot for the upgrade that moved them out of
            // compose.yaml.
            case SettingsWriter.Argument:
                exitCode = SettingsWriter.Write(args.Length > 1 ? args[1] : null);

                return true;

            default:
                return false;
        }
    }

    /// <summary>
    /// Prints the IANA name for a Windows time zone, or nothing when there is no answer.
    /// </summary>
    /// <remarks>
    /// The optional second argument is a two-letter region, and it earns its place: <c>Romance
    /// Standard Time</c> alone maps to <c>Europe/Paris</c>, and with <c>DK</c> to
    /// <c>Europe/Copenhagen</c>. The two behave identically - same offset, same rules - but a Dane
    /// reading Europe/Paris in their own <c>.env</c> would reasonably think it was a mistake.
    /// </remarks>
    /// <param name="args">The argument, the Windows zone identifier, and optionally a region.</param>
    /// <returns>Zero when a name was written, one when the zone was not recognised.</returns>
    private static int WriteIanaTimeZone(string[] args)
    {
        if (args.Length < 2)
        {
            return 1;
        }

        string windowsId = args[1];
        string? region = args.Length > 2 && !string.IsNullOrWhiteSpace(args[2]) ? args[2] : null;

        bool converted = region is null ?
            TimeZoneInfo.TryConvertWindowsIdToIanaId(windowsId, out string? iana) :
            TimeZoneInfo.TryConvertWindowsIdToIanaId(windowsId, region, out iana);

        if (!converted || string.IsNullOrEmpty(iana))
        {
            return 1;
        }

        Console.WriteLine(iana);

        return 0;
    }
}
