namespace Homespool.Host.Configuration;

/// <summary>
/// When a change to an editable setting starts being obeyed.
/// </summary>
/// <remarks>
/// <para>
/// <b>The grade is a property of the consumer, not of the setting.</b> It says where the value is
/// read: a consumer that reads <c>options.CurrentValue.X</c> at the point of use obeys a change on
/// its next use, and one that captured <c>options.Value</c> in its constructor never obeys it at all.
/// So a grade is a claim about code that can stop being true when that code is edited, which is why
/// it is declared here rather than left to a page's wording.
/// </para>
/// <para>
/// <b>Every editable setting carries one, and the page renders it.</b> The reason for the tier
/// existing at all is that a settings page which saves successfully and changes nothing is worse
/// than no page: the operator's next move is to doubt the setting rather than to restart. Saying
/// which of the three happens is the whole job.
/// </para>
/// <para>
/// <b>Members are pinned and zero is reserved</b>, per the convention for every enum here. Nothing
/// persists a grade today, but <see cref="Undefined"/> keeps a forgotten initialiser from silently
/// promising that a setting is live.
/// </para>
/// </remarks>
public enum SettingGrade
{
    /// <summary>
    /// Nobody said when this applies. <b>Never rendered and never valid on an allowlist entry</b> -
    /// a setting reaching a page with this grade is a setting whose consumer nobody checked.
    /// </summary>
    Undefined = 0,

    /// <summary>
    /// Obeyed on the consumer's next use, with no restart. Requires that every consumer reads it
    /// through <c>IOptionsMonitor</c> or <c>IOptionsSnapshot</c> rather than capturing
    /// <c>IOptions.Value</c>.
    /// </summary>
    Live = 1,

    /// <summary>
    /// Obeyed without a restart, but at a later moment the operator cannot see coming - the next
    /// retention sweep, a printer's next connection.
    /// </summary>
    /// <remarks>
    /// <b>Its own tier rather than a footnote on <see cref="Live"/>.</b> "Takes effect immediately"
    /// and "takes effect within the hour" are answers an operator acts on differently, and a setting
    /// whose sweep runs hourly looks broken for up to an hour if it claims the former. The moment
    /// itself is named per setting by <see cref="EditableSetting.AppliesWhenKey"/>, because the two
    /// deferred cases here defer to different things.
    /// </remarks>
    Deferred = 2,

    /// <summary>
    /// Written now, obeyed at the next service restart, because something is decided during startup
    /// from this value - which service is registered, or a value captured once when a loop begins.
    /// </summary>
    Restart = 3,
}
