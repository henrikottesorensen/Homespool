namespace Homespool.Host.Services;

/// <summary>One problem worth showing an administrator, already styled.</summary>
/// <param name="Message">The sentence, written by whatever produced it and shown verbatim.</param>
/// <param name="CssClass">The alert style.</param>
/// <param name="Heading">
/// What the message is announced as. Defaults to the health report's framing, since that is where
/// almost every item comes from - but a warning that the reader's own connection is unencrypted is
/// not a "service problem", and calling it one would make it sound like somebody else's job.
/// </param>
public sealed record HealthBannerItem(string Message, string CssClass, string Heading = "Service problem:");
