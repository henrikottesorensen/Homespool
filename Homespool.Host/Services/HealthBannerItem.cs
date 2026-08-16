namespace Homespool.Host.Services;

/// <summary>One problem worth showing an administrator, already styled.</summary>
/// <param name="Message">The sentence, written by whatever produced it and shown verbatim.</param>
/// <param name="CssClass">The alert style.</param>
/// <param name="HeadingKey">
/// The resource key for what the message is announced as. Defaults to the health report's framing,
/// since that is where almost every item comes from - but a warning that the reader's own connection
/// is unencrypted is not a "service problem", and calling it one would make it sound like somebody
/// else's job.
/// </param>
/// <remarks>
/// <b>The heading is a key and the message is not.</b> A default parameter has to be a compile-time
/// constant, so it cannot be a resource lookup - and the record is built by a static helper with no
/// localiser to hand. Carrying the key and resolving it in the view is what lets the banner be
/// localised without giving <see cref="HealthBanner"/> a dependency it has no other use for. The
/// message stays untranslated because it is a health check's own description, the same string
/// <c>/health</c> and the alert email carry.
/// </remarks>
public sealed record HealthBannerItem(string Message, string CssClass, string HeadingKey = "Health_ServiceProblem");
