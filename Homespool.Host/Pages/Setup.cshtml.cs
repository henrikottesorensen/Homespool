#nullable disable

using System;
using System.ComponentModel.DataAnnotations;
using System.Threading;
using System.Threading.Tasks;

using Homespool.Host.Services;
using Homespool.Model.Entities;

using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;

namespace Homespool.Host.Pages;

/// <summary>
/// First-run page that creates the administrator account, gated on the one-time bootstrap token
/// logged at startup. Reachable only while no administrator exists; it 404s the moment one does, so
/// it cannot be used to mint a second admin (AGENT-NOTES phase-1.5 decision 2).
/// </summary>
public class SetupModel : PageModel
{
    private readonly UserManager<HSUser> _userManager;
    private readonly IUserStore<HSUser> _userStore;
    private readonly IUserEmailStore<HSUser> _emailStore;
    private readonly SignInManager<HSUser> _signInManager;
    private readonly SetupState _setupState;
    private readonly AccountConfirmationPolicy _accountConfirmationPolicy;
    private readonly TeamService _teamService;
    private readonly UnitOfWork _unitOfWork;
    private readonly ILogger<SetupModel> _logger;

    public SetupModel(UserManager<HSUser> userManager,
                      IUserStore<HSUser> userStore,
                      SignInManager<HSUser> signInManager,
                      SetupState setupState,
                      AccountConfirmationPolicy accountConfirmationPolicy,
                      TeamService teamService,
                      UnitOfWork unitOfWork,
                      ILogger<SetupModel> logger)
    {
        _userManager = userManager;
        _userStore = userStore;
        _emailStore = GetEmailStore();
        _signInManager = signInManager;
        _setupState = setupState;
        _accountConfirmationPolicy = accountConfirmationPolicy;
        _teamService = teamService;
        _unitOfWork = unitOfWork;
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

        /// <summary>
        /// The sign-in name, and what the interface calls this person.
        /// </summary>
        /// <remarks>
        /// Only the length is checked here. The character set and uniqueness are Identity's
        /// <c>UserValidator</c>'s job, which runs on <c>CreateAsync</c> below and on every later change
        /// alike - restating either here would be a second copy of a rule to keep in step.
        /// </remarks>
        [Required]
        [StringLength(HSUser.UsernameMaxLength)]
        [Display(Name = "Username")]
        public string Username { get; set; }

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

        HSUser user = new();

        // Everything below - the Identity user, its admin role, and its default team (§15 — "admin
        // first, backfill team") - shares this one transaction, because a user without a team cannot
        // own a printer and a user without the admin role leaves setup stuck. Any early return before
        // CommitAsync leaves the transaction disposed uncommitted, which rolls back every write made
        // through it so far - no compensating delete needed on any failure path.
        await using IDbContextTransaction transaction = await _unitOfWork.BeginTransactionAsync(CancellationToken.None);

        try
        {
            await _userStore.SetUserNameAsync(user, Input.Username, CancellationToken.None);
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
                AddErrors(roleResult);

                return Page();
            }

            await _teamService.AddDefaultTeamAsync(user.Id, DateTimeOffset.UtcNow, CancellationToken.None);

            await transaction.CommitAsync(CancellationToken.None);
        }
        catch (DbUpdateException ex)
        {
            _logger.LogError(ex, "Failed to complete first-time setup; rolling back the account.");
            ModelState.AddModelError(string.Empty, "Could not complete setup. Please try again.");

            return Page();
        }

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

    private IUserEmailStore<HSUser> GetEmailStore()
    {
        if (!_userManager.SupportsUserEmail)
        {
            throw new System.NotSupportedException("The user store requires email support.");
        }

        return (IUserEmailStore<HSUser>)_userStore;
    }
}
