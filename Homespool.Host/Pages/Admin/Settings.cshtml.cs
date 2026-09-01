using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Localization;

using Homespool.Host.Accounts;
using Homespool.Host.Configuration;
using Homespool.Host.Localisation;
using Homespool.Host.Mail;
using Homespool.Host.Middleware;
using Homespool.Model.Entities;

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
    private readonly UserManager<HSUser> _users;
    private readonly SmtpConnectivityCheck _mail;
    private readonly IStringLocalizer<SharedResource> _localiser;

    /// <summary>Creates the page model.</summary>
    /// <param name="store">Reads and writes the settings.</param>
    /// <param name="users">Answers whether the administrator doing this has an authenticator.</param>
    /// <param name="mail">Tries a mail server on request.</param>
    /// <param name="localiser">Page text.</param>
    public SettingsModel(SettingsStore store,
                         UserManager<HSUser> users,
                         SmtpConnectivityCheck mail,
                         IStringLocalizer<SharedResource> localiser)
    {
        _store = store;
        _users = users;
        _mail = mail;
        _localiser = localiser;
    }

    /// <summary>The submitted values, keyed by setting path.</summary>
    [BindProperty]
    public Dictionary<string, string?> Values { get; set; } = [];

    /// <summary>Paths whose consequences have been read and agreed to on this post.</summary>
    [BindProperty]
    public List<string> Confirmed { get; set; } = [];

    /// <summary>
    /// Settings this post would turn on that nobody has agreed to yet. Non-empty means nothing was
    /// saved and the page is asking.
    /// </summary>
    public IReadOnlyList<EditableSetting> AwaitingConfirmation { get; private set; } = [];

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
    public async Task<IActionResult> OnPost()
    {
        IReadOnlyDictionary<string, string> refusals = await RefusalsAsync();

        if (refusals.Count > 0)
        {
            Errors = refusals;
            StatusMessage = _localiser["Settings_NotSaved"];
            StatusSuccess = false;
            Sections = Grouped();

            return Page();
        }

        AwaitingConfirmation = Unagreed();

        if (AwaitingConfirmation.Count > 0)
        {
            // Nothing is written on this pass. The submitted values are carried back into the form so
            // the answer is applied to exactly what was asked about, rather than to whatever the page
            // looks like by the time somebody agrees.
            Sections = Grouped();

            return Page();
        }

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
    /// Tries the mail server the form currently describes, without saving anything.
    /// </summary>
    /// <remarks>
    /// <b>The values on the form, not the ones in force.</b> Mail settings need a restart, so the
    /// running configuration is what the deployment is doing rather than what somebody is asking
    /// about - and after a save the two differ, which is exactly when this button gets pressed. A
    /// password nobody was shown is taken from what is stored, so the mask does not have to be
    /// re-typed to test the rest.
    /// </remarks>
    /// <param name="cancellationToken">Abandons the attempt if the request goes away.</param>
    /// <returns>The page, with the outcome on it.</returns>
    public async Task<IActionResult> OnPostTestMail(CancellationToken cancellationToken)
    {
        SmtpOptions candidate = _store.CandidateFor<SmtpOptions>(Values);

        SmtpCheckResult result = await _mail.RunAsync(candidate, cancellationToken);

        StatusSuccess = result.Succeeded;
        StatusMessage = result.Succeeded ?
            _localiser["Settings_MailTestWorked"] :
            string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                _localiser["Settings_MailTestFailed"],
                result.Error);

        Sections = Grouped();

        return Page();
    }

    /// <summary>
    /// A byte count as a person reads it, or null when the setting is not one.
    /// </summary>
    /// <remarks>
    /// <b>Recognised by the property name rather than a flag on the allowlist</b>, because the
    /// convention already carries it - a setting measured in bytes is named for it, and its label
    /// says so too. A presentation detail is not worth another column somebody has to remember to
    /// fill in.
    /// </remarks>
    /// <param name="setting">The setting being rendered.</param>
    /// <returns>Something like <c>512 MB</c>, or null.</returns>
    public string? SizeHint(EditableSetting setting)
    {
        System.ArgumentNullException.ThrowIfNull(setting);

        if (!setting.Key.EndsWith("Bytes", System.StringComparison.Ordinal))
        {
            return null;
        }

        return long.TryParse(Values.GetValueOrDefault(setting.Path),
                             System.Globalization.NumberStyles.Integer,
                             System.Globalization.CultureInfo.InvariantCulture,
                             out long bytes) && bytes >= 0 ?
            ByteSize.Format(bytes, _localiser) :
            null;
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

    /// <summary>
    /// Changes this administrator may not make, whatever they agree to.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Requiring an authenticator is refused unless the person asking already has one</b> (Henrik).
    /// The requirement applies to the account rather than the session, so an administrator without one
    /// is redirected to enrol before they can reach any page - including this one, and including the
    /// way back. Turning it on would be locking yourself out of the room you are standing in.
    /// </para>
    /// <para>
    /// <b>A refusal rather than a warning</b>, because there is nothing to weigh: enrolling first
    /// costs a minute, reaches the same state, and proves the flow works before it is imposed on
    /// everybody else.
    /// </para>
    /// <para>
    /// One case, written as one case. A second earns a general shape; one does not.
    /// </para>
    /// </remarks>
    private async Task<IReadOnlyDictionary<string, string>> RefusalsAsync()
    {
        string path = $"{SecurityOptions.SectionName}:{nameof(SecurityOptions.RequireTwoFactor)}";

        if (!IsOn(Values.GetValueOrDefault(path)) || IsOn(_store.Current().GetValueOrDefault(path)))
        {
            return new Dictionary<string, string>();
        }

        HSUser? user = await _users.GetUserAsync(User);

        if (user is not null && await _users.GetTwoFactorEnabledAsync(user))
        {
            return new Dictionary<string, string>();
        }

        return new Dictionary<string, string>
        {
            [path] = _localiser["Settings_Refuse_Security_RequireTwoFactor"],
        };
    }

    /// <summary>
    /// The settings this post turns on that carry a consequence nobody has agreed to yet.
    /// </summary>
    /// <remarks>
    /// <b>Only the off-to-on transition asks.</b> A setting already on, or being turned off, or
    /// absent from the post, is not a decision anybody needs talking through - and asking about the
    /// safe direction is how a person learns to click past the question that matters.
    /// </remarks>
    private IReadOnlyList<EditableSetting> Unagreed()
    {
        IReadOnlyDictionary<string, string> current = _store.Current();

        return
        [
            .. EditableSettings.All.Where(
                setting => setting.NeedsConfirmingToEnable
                           && IsOn(Values.GetValueOrDefault(setting.Path))
                           && !IsOn(current.GetValueOrDefault(setting.Path))
                           && !Confirmed.Contains(setting.Path, System.StringComparer.Ordinal)),
        ];
    }

    /// <summary>
    /// Whether a value counts as switched on.
    /// </summary>
    /// <remarks>
    /// <b>Not every switch is a boolean.</b> Mail is turned on by naming a server, so "on" has to mean
    /// "carries an answer" rather than "is true" - empty is off for a host exactly as false is off for
    /// a flag, and both are the state a deployment has without anybody choosing it.
    /// </remarks>
    private static bool IsOn(string? value)
    {
        return !string.IsNullOrWhiteSpace(value)
               && !string.Equals(value, "false", System.StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<IGrouping<string, EditableSetting>> Grouped()
    {
        return [.. EditableSettings.All.GroupBy(setting => setting.Group)];
    }

    private void Load()
    {
        Sections = Grouped();
        Values = _store.Current().ToDictionary(pair => pair.Key, pair => (string?)pair.Value);
    }
}
