#nullable disable

using System.ComponentModel.DataAnnotations;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Logging;

using PrinterService.Api.Services;
using PrinterService.Model.Entities;

namespace PrinterService.Api.Pages;

/// <summary>
/// First-run page that creates the administrator account, gated on the one-time bootstrap token
/// logged at startup. Reachable only while no administrator exists; it 404s the moment one does, so
/// it cannot be used to mint a second admin (AGENT-NOTES phase-1.5 decision 2).
/// </summary>
public class SetupModel : PageModel
{
    private readonly UserManager<PSUser> _userManager;
    private readonly IUserStore<PSUser> _userStore;
    private readonly IUserEmailStore<PSUser> _emailStore;
    private readonly SignInManager<PSUser> _signInManager;
    private readonly SetupState _setupState;
    private readonly AccountConfirmationPolicy _accountConfirmationPolicy;
    private readonly ILogger<SetupModel> _logger;

    public SetupModel(
        UserManager<PSUser> userManager,
        IUserStore<PSUser> userStore,
        SignInManager<PSUser> signInManager,
        SetupState setupState,
        AccountConfirmationPolicy accountConfirmationPolicy,
        ILogger<SetupModel> logger)
    {
        _userManager = userManager;
        _userStore = userStore;
        _emailStore = GetEmailStore();
        _signInManager = signInManager;
        _setupState = setupState;
        _accountConfirmationPolicy = accountConfirmationPolicy;
        _logger = logger;
    }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public class InputModel
    {
        [Required]
        [EmailAddress]
        [Display(Name = "Email")]
        public string Email { get; set; }

        [Required]
        [StringLength(100, ErrorMessage = "The {0} must be at least {2} and at max {1} characters long.", MinimumLength = 6)]
        [DataType(DataType.Password)]
        [Display(Name = "Password")]
        public string Password { get; set; }

        [DataType(DataType.Password)]
        [Display(Name = "Confirm password")]
        [Compare(nameof(Password), ErrorMessage = "The password and confirmation password do not match.")]
        public string ConfirmPassword { get; set; }

        [Required]
        [DataType(DataType.Password)]
        [Display(Name = "Setup token")]
        public string Token { get; set; }
    }

    public IActionResult OnGet()
    {
        if (_setupState.IsComplete)
        {
            return NotFound();
        }

        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (_setupState.IsComplete)
        {
            return NotFound();
        }

        if (!ModelState.IsValid)
        {
            return Page();
        }

        // Token check before any user work, and phrased so a valid token against an already-completed
        // setup reads the same as a wrong one - no oracle for either.
        if (!_setupState.Verify(Input.Token))
        {
            ModelState.AddModelError(string.Empty, "The setup token is invalid, or first-time setup is already complete.");

            return Page();
        }

        PSUser user = new();

        await _userStore.SetUserNameAsync(user, Input.Email, CancellationToken.None);
        await _emailStore.SetEmailAsync(user, Input.Email, CancellationToken.None);

        _accountConfirmationPolicy.Apply(user);

        IdentityResult createResult = await _userManager.CreateAsync(user, Input.Password);

        if (!createResult.Succeeded)
        {
            AddErrors(createResult);

            return Page();
        }

        IdentityResult roleResult = await _userManager.AddToRoleAsync(user, AdminBootstrap.AdminRole);

        if (!roleResult.Succeeded)
        {
            // The account exists but is not an administrator, which would leave setup stuck: a retry
            // with the same email fails on the duplicate. Undo the half-done creation so the operator
            // can simply try again.
            await _userManager.DeleteAsync(user);
            AddErrors(roleResult);

            return Page();
        }

        // TODO(phase-1.5 step 5): mint this admin's default Team + TeamMember here once those entities
        // exist. Deferred deliberately - "admin first, backfill team" (Henrik, 2026-07-23). Printer
        // claiming (step 7) cannot resolve an owner until then, but nothing claims yet.

        _setupState.MarkComplete();

        _logger.LogInformation("First-time setup completed; administrator account created for {Email}.", Input.Email);

        await _signInManager.SignInAsync(user, isPersistent: false);

        return LocalRedirect("~/");
    }

    private void AddErrors(IdentityResult result)
    {
        foreach (IdentityError error in result.Errors)
        {
            ModelState.AddModelError(string.Empty, error.Description);
        }
    }

    private IUserEmailStore<PSUser> GetEmailStore()
    {
        if (!_userManager.SupportsUserEmail)
        {
            throw new System.NotSupportedException("The user store requires email support.");
        }

        return (IUserEmailStore<PSUser>)_userStore;
    }
}
