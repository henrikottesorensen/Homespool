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
    string? AppliesWhenKey = null)
{
    /// <summary>
    /// The configuration path this setting is read and written at, in <c>Section:Key</c> form.
    /// </summary>
    public string Path => $"{Section}:{Key}";
}
