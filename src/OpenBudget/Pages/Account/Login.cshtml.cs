using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Localization;
using OpenBudget.Models;

namespace OpenBudget.AccountPages;

[AllowAnonymous]
public class LoginModel : PageModel
{
    private readonly SignInManager<AppUser> _signIn;
    private readonly UserManager<AppUser> _users;
    private readonly IStringLocalizer<LoginModel> _l;
    private readonly ILogger<LoginModel> _logger;

    public LoginModel(SignInManager<AppUser> signIn, UserManager<AppUser> users, IStringLocalizer<LoginModel> l, ILogger<LoginModel> logger)
    {
        _signIn = signIn;
        _users = users;
        _l = l;
        _logger = logger;
    }

    // A string, not a Uri: it round-trips through a form field and is validated by Url.IsLocalUrl.
#pragma warning disable CA1056
    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }
#pragma warning restore CA1056

    [BindProperty]
    public string? UserName { get; set; }

    [BindProperty]
    public string? Password { get; set; }

    public string? Error { get; private set; }

    public IActionResult OnGet() =>
        User.Identity?.IsAuthenticated == true ? LocalRedirect(SafeReturnUrl()) : Page();

    /// <summary>Only local URLs are honoured as post-login destinations (no open redirect).</summary>
    private string SafeReturnUrl() =>
        !string.IsNullOrEmpty(ReturnUrl) && Url.IsLocalUrl(ReturnUrl) ? ReturnUrl : "/";

    public async Task<IActionResult> OnPostAsync()
    {
        if (string.IsNullOrWhiteSpace(UserName) || string.IsNullOrEmpty(Password))
        {
            Error = _l["Login_Invalid"].Value;
            return Page();
        }

        var user = await _users.FindByNameAsync(UserName.Trim());
        if (user == null)
        {
            // Burn comparable time so unknown names aren't distinguishable by latency.
            _users.PasswordHasher.HashPassword(new AppUser(), Password);
            Error = _l["Login_Invalid"].Value;
            return Page();
        }

        // Lockout after repeated failures (Identity defaults); always a persistent 30-day sliding session.
        var result = await _signIn.PasswordSignInAsync(user, Password, isPersistent: true, lockoutOnFailure: true);
        if (result.Succeeded) return LocalRedirect(SafeReturnUrl());

        if (result.IsLockedOut)
        {
            _logger.LogWarning("User {User} is locked out after repeated failed logins.", user.UserName);
            Error = _l["Login_LockedOut"].Value;
        }
        else
        {
            Error = _l["Login_Invalid"].Value;
        }
        return Page();
    }

    /// <summary>Passkey assertion options for a username-less (discoverable credential) sign-in.</summary>
    public async Task<IActionResult> OnPostPasskeyOptionsAsync()
    {
        var optionsJson = await _signIn.MakePasskeyRequestOptionsAsync(user: null);
        return Content(optionsJson, "application/json");
    }

    public async Task<IActionResult> OnPostPasskeyLoginAsync(string? credentialJson)
    {
        if (string.IsNullOrEmpty(credentialJson))
        {
            Error = _l["Passkey_Failed"].Value;
            return Page();
        }

        var result = await _signIn.PasskeySignInAsync(credentialJson);
        if (result.Succeeded) return LocalRedirect(SafeReturnUrl());

        Error = result.IsLockedOut ? _l["Login_LockedOut"].Value : _l["Passkey_Failed"].Value;
        return Page();
    }
}
