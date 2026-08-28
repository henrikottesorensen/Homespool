using System;

namespace Homespool.Host.Configuration;

/// <summary>
/// One setting an administrator may change from the application, and what changing it does.
/// </summary>
/// <param name="OptionsType">
/// The options class the setting belongs to. Held as a type rather than a name so that
/// <see cref="Section"/> and <see cref="Key"/> can be checked against the real property.
/// </param>
/// <param name="Section">The configuration section, matching the options class's <c>SectionName</c>.</param>
/// <param name="Key">The property name on <paramref name="OptionsType"/>, spelled exactly.</param>
/// <param name="Grade">When a change starts being obeyed.</param>
/// <param name="IsSecret">
/// Whether the stored value is a credential. A secret is never rendered back to the browser and is
/// stored as ciphertext rather than in the clear.
/// </param>
/// <param name="AppliesWhenKey">
/// A localisation key naming the moment a <see cref="SettingGrade.Deferred"/> setting starts being
/// obeyed. Null for every other grade, whose moment the grade itself already names.
/// </param>
/// <param name="DisplayGroup">
/// The heading this appears under, when that is not its own <paramref name="Section"/>. Null for
/// almost everything.
/// </param>
/// <param name="ConfirmOnEnableKey">
/// A localisation key naming what turning this on does, when that is not visible from the outcome.
/// Set only for the settings where somebody should be asked; null everywhere else.
/// </param>
/// <param name="DisplaySubgroup">
/// A subheading within the group, for a group large enough that one list of fields stops being
/// readable. Null for a group that needs none.
/// </param>
/// <remarks>
/// <para>
/// <b>An allowlist entry, not a description of configuration.</b> The 65 options properties are not
/// all an operator's business - listener ports must agree with what Docker publishes, directory
/// paths with what is mounted, and the printer address is burned into a minted certificate. Those
/// stay in <c>.env</c>, and the way they stay there is by not appearing on this list. A page that
/// enumerated bound properties instead would eventually offer somebody a browser control that
/// repoints a volume.
/// </para>
/// <para>
/// <b><see cref="Path"/> is the one spelling that reaches configuration</b>, so nothing else has to
/// agree about how a section and a key are joined.
/// </para>
/// </remarks>
public sealed record EditableSetting(
    Type OptionsType,
    string Section,
    string Key,
    SettingGrade Grade,
    bool IsSecret = false,
    string? AppliesWhenKey = null,
    string? DisplayGroup = null,
    string? DisplaySubgroup = null,
    string? ConfirmOnEnableKey = null)
{
    /// <summary>
    /// The configuration path this setting is read and written at, in <c>Section:Key</c> form.
    /// </summary>
    public string Path => $"{Section}:{Key}";

    /// <summary>
    /// The path the value is actually stored under in the settings file.
    /// </summary>
    /// <remarks>
    /// The same as <see cref="Path"/> for everything except a secret, which is stored beside its
    /// property as ciphertext under a <c>Protected</c>-prefixed name — <c>Smtp:Password</c> is read
    /// from <c>Smtp:ProtectedPassword</c>. Keeping the two apart is what lets a plaintext value that
    /// arrived some other way be recognised and adopted rather than mistaken for ciphertext.
    /// </remarks>
    public string StoredPath => IsSecret ? $"{Section}:Protected{Key}" : Path;

    /// <summary>
    /// The heading this setting is shown under.
    /// </summary>
    /// <remarks>
    /// <b>Separate from <see cref="Section"/> because a configuration section is not a subject.</b>
    /// Which class a setting is bound from is an implementation fact, and letting it decide the
    /// headings put two one-setting groups next to each other that were both plainly about accounts.
    /// This only moves where a setting is <i>rendered</i>; it is stored, validated and read at
    /// <see cref="Path"/> either way, so grouping can be rearranged without touching a file anybody
    /// has written.
    /// </remarks>
    public string Group => DisplayGroup ?? Section;

    /// <summary>
    /// Whether turning this on has to be agreed to first.
    /// </summary>
    /// <remarks>
    /// <b>On the dangerous direction only</b>, following the live-view prompt: turning something off
    /// is undoable and asking about it is how people learn to click through the question that
    /// matters. So this asks when a value goes from off to on, and never when it goes back.
    /// </remarks>
    public bool NeedsConfirmingToEnable => ConfirmOnEnableKey is not null;
}
