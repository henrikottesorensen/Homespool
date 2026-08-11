using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.Extensions.Localization;

using Homespool.Host.Localisation;
using Homespool.Model.Entities;

namespace Homespool.Host.Pages.Account.Manage;

/// <summary>
/// Choosing the language Homespool speaks to you in.
/// </summary>
/// <remarks>
/// <para>
/// <b>The one surface that proves the pipeline switches.</b> Everything else in the localisation
/// work is infrastructure that cannot be seen to work; this is the page that makes it falsifiable.
/// </para>
/// <para>
/// <b>Stored on the account rather than in a cookie</b>, which is what makes it apply to email as
/// well as to pages — see <see cref="UserCultures"/>. A cookie would follow the browser and leave
/// every alert in the deployment's default language.
/// </para>
/// </remarks>
public class LanguageModel : PageModel
{
    /// <summary>The form value meaning "no stored preference".</summary>
    /// <remarks>
    /// Empty rather than absent, because a radio group has to be able to post the choice of
    /// <i>not</i> choosing. Null in the column is what that becomes.
    /// </remarks>
    public const string FollowBrowser = "";

    private readonly UserManager<HSUser> _userManager;
    private readonly SignInManager<HSUser> _signInManager;
    private readonly IStringLocalizer<SharedResource> _localiser;
    private readonly TimeProvider _time;

    public LanguageModel(UserManager<HSUser> userManager,
                         SignInManager<HSUser> signInManager,
                         IStringLocalizer<SharedResource> localiser,
                         TimeProvider time)
    {
        _userManager = userManager;
        _signInManager = signInManager;
        _localiser = localiser;
        _time = time;
    }

    /// <summary>The culture currently stored, or <see cref="FollowBrowser"/> when there is none.</summary>
    [BindProperty]
    public string Selected { get; set; } = FollowBrowser;

    [TempData]
    public string? StatusMessage { get; set; }

    /// <summary>The languages offered, plus the "follow my browser" option at the top.</summary>
    public IReadOnlyList<SelectListItem> Options { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync()
    {
        HSUser? user = await _userManager.GetUserAsync(User);
        if (user is null)
        {
            return NotFound();
        }

        Selected = user.Language ?? FollowBrowser;
        BuildOptions();

        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        HSUser? user = await _userManager.GetUserAsync(User);
        if (user is null)
        {
            return NotFound();
        }

        // Anything not on the offered list becomes "follow my browser" rather than an error. The
        // only way to post something else is to have edited the form, and there is nothing here
        // worth writing an error message about.
        user.Language = SupportedLanguages.Resolve(Selected);

        IdentityResult result = await _userManager.UpdateAsync(user);
        if (!result.Succeeded)
        {
            BuildOptions();
            foreach (IdentityError error in result.Errors)
            {
                ModelState.AddModelError(string.Empty, error.Description);
            }

            return Page();
        }

        // The cookie carries the security stamp, not the language, so this is not strictly needed
        // for the preference to take effect - the next request reads the column. It is here because
        // an account change that skips it is the kind that goes stale in one browser and not
        // another.
        await _signInManager.RefreshSignInAsync(user);

        // Written in the language just chosen rather than the one the page was rendered in, so the
        // confirmation is itself the evidence that the choice took.
        StatusMessage = UserCultures.InCulture(
            user.Language,
            () => _localiser["Language_Saved"].Value);

        return RedirectToPage();
    }

    /// <summary>
    /// The radio options, with each language named in itself.
    /// </summary>
    private void BuildOptions()
    {
        List<SelectListItem> options =
        [
            new(_localiser["Language_FollowBrowser"], FollowBrowser),
        ];

        // Local time, not UTC: the question is whether it is April Fools' where this Homespool is
        // installed. See SupportedLanguages.DisplayNamesOn.
        IReadOnlyDictionary<string, string> names =
            SupportedLanguages.DisplayNamesOn(_time.GetLocalNow());

        options.AddRange(SupportedLanguages.CultureNames.Select(culture => new SelectListItem(names[culture], culture)));

        Options = options;
    }
}
