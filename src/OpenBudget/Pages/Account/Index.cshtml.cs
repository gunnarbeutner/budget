using System.Buffers.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using OpenBudget.Models;

namespace OpenBudget.AccountPages;

public sealed record PasskeyRow(string Id, string Name, DateTime CreatedAt);

/// <summary>The signed-in user's account page: change password, manage passkeys, log out.</summary>
public class AccountIndexModel : PageModel
{
    private readonly SignInManager<AppUser> _signIn;
    private readonly UserManager<AppUser> _users;
    private readonly IStringLocalizer<LoginModel> _l;
    private readonly OpenBudget.Services.SystemDbContextFactory _db;

    public AccountIndexModel(SignInManager<AppUser> signIn, UserManager<AppUser> users, IStringLocalizer<LoginModel> l,
        OpenBudget.Services.SystemDbContextFactory db)
    {
        _signIn = signIn;
        _users = users;
        _db = db;
        _l = l;
    }

    /// <summary>While Get started holds the user, the account page leads there (only signing out stays open).</summary>
    public override async Task OnPageHandlerExecutionAsync(
        Microsoft.AspNetCore.Mvc.Filters.PageHandlerExecutingContext context, Microsoft.AspNetCore.Mvc.Filters.PageHandlerExecutionDelegate next)
    {
        await using var db = await _db.CreateDbContextAsync();
        var userId = _users.GetUserId(User);
        var state = await db.UserDataStates.AsNoTracking().FirstOrDefaultAsync(s => s.UserId == userId);
        if (userId != null && !OpenBudget.Services.OnboardingState.IsFinished(state))
        {
            context.Result = LocalRedirect("/get-started");
            return;
        }
        await next();
    }

    public string UserName { get; private set; } = "";
    public string? DisplayName { get; private set; }
    public bool IsAdmin { get; private set; }
    public List<PasskeyRow> Passkeys { get; private set; } = new();
    public List<string> Errors { get; } = new();

    [TempData]
    public string? Message { get; set; }

    public async Task<IActionResult> OnGetAsync()
    {
        var user = await _users.GetUserAsync(User);
        if (user == null) return RedirectToPage("/Account/Login");
        await LoadAsync(user);
        return Page();
    }

    public async Task<IActionResult> OnPostChangePasswordAsync(string? currentPassword, string? newPassword, string? confirmPassword)
    {
        var user = await _users.GetUserAsync(User);
        if (user == null) return RedirectToPage("/Account/Login");

        if (string.IsNullOrEmpty(currentPassword) || string.IsNullOrEmpty(newPassword))
        {
            Errors.Add(_l["Password_Required"].Value);
        }
        else if (newPassword != confirmPassword)
        {
            Errors.Add(_l["Password_Mismatch"].Value);
        }
        else
        {
            var result = await _users.ChangePasswordAsync(user, currentPassword, newPassword);
            if (result.Succeeded)
            {
                // New security stamp: sign this browser back in so only the *other* sessions end.
                await _signIn.RefreshSignInAsync(user);
                Message = _l["Password_Changed"].Value;
                return RedirectToPage();
            }
            Errors.AddRange(result.Errors.Select(e => e.Description));
        }

        await LoadAsync(user);
        return Page();
    }

    public async Task<IActionResult> OnPostDisplayNameAsync(string? displayName)
    {
        var user = await _users.GetUserAsync(User);
        if (user == null) return RedirectToPage("/Account/Login");

        user.DisplayName = AppUser.NormalizeDisplayName(displayName);
        var result = await _users.UpdateAsync(user);
        if (result.Succeeded)
        {
            Message = _l["DisplayName_Saved"].Value;
            return RedirectToPage();
        }
        Errors.AddRange(result.Errors.Select(e => e.Description));
        await LoadAsync(user);
        return Page();
    }

    /// <summary>Creation options for a new passkey of the signed-in user.</summary>
    public async Task<IActionResult> OnPostPasskeyCreationOptionsAsync()
    {
        var user = await _users.GetUserAsync(User);
        if (user == null) return Unauthorized();

        var name = user.UserName ?? user.Id;
        var json = await _signIn.MakePasskeyCreationOptionsAsync(new PasskeyUserEntity
        {
            Id = user.Id,
            Name = name,
            DisplayName = name,
        });
        return Content(json, "application/json");
    }

    public async Task<IActionResult> OnPostAddPasskeyAsync(string? credentialJson, string? passkeyName)
    {
        var user = await _users.GetUserAsync(User);
        if (user == null) return RedirectToPage("/Account/Login");

        if (string.IsNullOrEmpty(credentialJson))
        {
            Errors.Add(_l["Passkey_RegisterFailed"].Value);
        }
        else
        {
            var attestation = await _signIn.PerformPasskeyAttestationAsync(credentialJson);
            if (!attestation.Succeeded || attestation.UserEntity.Id != user.Id)
            {
                Errors.Add(_l["Passkey_RegisterFailed"].Value);
            }
            else
            {
                var name = string.IsNullOrWhiteSpace(passkeyName) ? _l["Passkeys_DefaultName"].Value : passkeyName.Trim();
                attestation.Passkey.Name = name.Length > 60 ? name[..60] : name;
                var saved = await _users.AddOrUpdatePasskeyAsync(user, attestation.Passkey);
                if (saved.Succeeded)
                {
                    Message = _l["Passkeys_Added"].Value;
                    return RedirectToPage();
                }
                Errors.AddRange(saved.Errors.Select(e => e.Description));
            }
        }

        await LoadAsync(user);
        return Page();
    }

    public async Task<IActionResult> OnPostDeletePasskeyAsync(string? credentialId)
    {
        var user = await _users.GetUserAsync(User);
        if (user == null) return RedirectToPage("/Account/Login");

        if (!string.IsNullOrEmpty(credentialId) && Base64Url.IsValid(credentialId))
        {
            // Scoped to the signed-in user's own passkeys by the store (UserId + CredentialId).
            var result = await _users.RemovePasskeyAsync(user, Base64Url.DecodeFromChars(credentialId));
            if (result.Succeeded) Message = _l["Passkeys_Removed"].Value;
        }
        return RedirectToPage();
    }

    private async Task LoadAsync(AppUser user)
    {
        UserName = user.UserName ?? "";
        DisplayName = user.DisplayName;
        IsAdmin = user.IsAdmin;
        var passkeys = await _users.GetPasskeysAsync(user);
        Passkeys = passkeys
            .OrderBy(p => p.CreatedAt)
            .Select(p => new PasskeyRow(Base64Url.EncodeToString(p.CredentialId), string.IsNullOrEmpty(p.Name) ? _l["Passkeys_DefaultName"].Value : p.Name, p.CreatedAt.UtcDateTime))
            .ToList();
    }
}
