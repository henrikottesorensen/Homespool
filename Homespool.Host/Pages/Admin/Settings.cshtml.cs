using System.Collections.Generic;
using System.Linq;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Localization;

using Homespool.Host.Accounts;
using Homespool.Host.Configuration;
using Homespool.Host.Localisation;

namespace Homespool.Host.Pages.Admin;

/// <summary>
/// The deployment settings an administrator may change, and the one place they are changed.
/// </summary>
/// <remarks>
/// <para>
/// <b>Administrator, following the certificate page rather than a per-printer capability.</b> Nothing
/// here belongs to a printer or a team — these decide how the whole deployment behaves, so the right
/// is the deployment-wide one.
/// </para>
/// <para>
/// <b>The page renders the allowlist rather than a hand-written form.</b> Adding a setting means
/// adding a row there, and the grade it declares is what produces the badge and the sentence saying
/// when the change takes effect. A form written by hand would drift from the grades the moment
/// somebody added the second one.
/// </para>
/// <para>
/// <b>What is deliberately absent is everything in <c>.env</c>.</b> Ports must agree with what Docker
/// publishes and paths with what is mounted, so a browser control that changed them would be a
/// control that breaks the deployment on the next restart with no clue why.
/// </para>
/// </remarks>
[Authorize(Roles = AdminBootstrap.AdminRole)]
public class SettingsModel : PageModel
{
    private readonly SettingsStore _store;
    private readonly IStringLocalizer<SharedResource> _localiser;

    /// <summary>Creates the page model.</summary>
    /// <param name="store">Reads and writes the settings.</param>
    /// <param name="localiser">Page text.</param>
    public SettingsModel(SettingsStore store, IStringLocalizer<SharedResource> localiser)
    {
        _store = store;
        _localiser = localiser;
    }

    /// <summary>The submitted values, keyed by setting path.</summary>
    [BindProperty]
    public Dictionary<string, string?> Values { get; set; } = [];

    /// <summary>The settings to render, in allowlist order, grouped by section.</summary>
    public IReadOnlyList<IGrouping<string, EditableSetting>> Sections { get; private set; } = [];

    /// <summary>Per-setting validation failures from the last save.</summary>
    public IReadOnlyDictionary<string, string> Errors { get; private set; } =
        new Dictionary<string, string>();

    /// <summary>Outcome of the last save, if there was one.</summary>
    public string? StatusMessage { get; private set; }

    /// <summary>Whether <see cref="StatusMessage"/> reports success.</summary>
    public bool StatusSuccess { get; private set; }

    /// <summary>Shows the current settings.</summary>
    public void OnGet()
    {
        Load();
    }

    /// <summary>Applies the submitted settings, or reports why it did not.</summary>
    /// <returns>The page, either way — a redirect would lose the field-level errors.</returns>
    public IActionResult OnPost()
    {
        SettingsSaveResult result = _store.Save(Values);

        if (result.Saved)
        {
            StatusMessage = _localiser["Settings_Saved"];
            StatusSuccess = true;

            Load();

            return Page();
        }

        Errors = result.Errors;
        StatusMessage = _localiser["Settings_NotSaved"];
        StatusSuccess = false;

        // The submitted values are kept so somebody can see and correct what they typed; only the
        // grouping is reloaded.
        Sections = Grouped();

        return Page();
    }

    /// <summary>
    /// The sentence telling somebody when a change will be obeyed.
    /// </summary>
    /// <param name="setting">The setting being rendered.</param>
    /// <returns>Localised text.</returns>
    public string AppliesWhen(EditableSetting setting)
    {
        System.ArgumentNullException.ThrowIfNull(setting);

        return setting.Grade switch
        {
            SettingGrade.Live => _localiser["Settings_AppliesImmediately"],
            SettingGrade.Deferred => _localiser[setting.AppliesWhenKey!],
            SettingGrade.Restart => _localiser["Settings_AppliesOnRestart"],
            _ => string.Empty,
        };
    }

    private static IReadOnlyList<IGrouping<string, EditableSetting>> Grouped()
    {
        return [.. EditableSettings.All.GroupBy(setting => setting.Section)];
    }

    private void Load()
    {
        Sections = Grouped();
        Values = _store.Current().ToDictionary(pair => pair.Key, pair => (string?)pair.Value);
    }
}
