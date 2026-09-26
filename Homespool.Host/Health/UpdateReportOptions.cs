namespace Homespool.Host.Health;

/// <summary>
/// Where the host's image update check leaves its report, bound from the <c>UpdateReport</c>
/// configuration section.
/// </summary>
/// <remarks>
/// <para>
/// <b>The application reads this; it never checks for updates itself.</b> The check runs on the host,
/// from <c>update-check/</c> in the repository, because only the host knows which image each container
/// is running and only the host could ever act on the answer. It writes the report into a named volume
/// that <c>compose.yaml</c> mounts read-only at this path.
/// </para>
/// <para>
/// A deployment with no check installed has the volume and no file in it, which
/// <see cref="UpdateReportHealthCheck"/> reads as nothing to say, not as a fault.
/// </para>
/// </remarks>
public class UpdateReportOptions
{
    public const string SectionName = "UpdateReport";

    /// <summary>The report's path inside the container, where <c>compose.yaml</c> mounts it.</summary>
    public string Path { get; set; } = "/var/lib/homespool/update-check.json";

    /// <summary>
    /// How old a report may be before its age is itself the finding: the check has stopped running.
    /// </summary>
    /// <remarks>
    /// Three days, because the check runs daily at a random moment within six hours of midnight, so
    /// consecutive reports can already be thirty hours apart without anything being wrong.
    /// </remarks>
    public int StaleAfterDays { get; set; } = 3;
}
