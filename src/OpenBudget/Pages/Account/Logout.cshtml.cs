using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using OpenBudget.Models;

namespace OpenBudget.AccountPages;

public class LogoutModel : PageModel
{
    private readonly SignInManager<AppUser> _signIn;

    public LogoutModel(SignInManager<AppUser> signIn) => _signIn = signIn;

    /// <summary>GET only shows a confirmation; signing out is a POST so it is CSRF-protected.</summary>
    public void OnGet()
    {
        // Nothing to load: the page is just the confirmation form.
    }

    public async Task<IActionResult> OnPostAsync()
    {
        await _signIn.SignOutAsync();
        return RedirectToPage("/Account/Login");
    }
}
