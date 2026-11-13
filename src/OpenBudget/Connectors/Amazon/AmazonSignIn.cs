namespace OpenBudget.Connectors.Amazon;

/// <summary>
/// The amazon.de sign-in flow, all its selectors in one place. The e-mail page (#ap_email, #continue) is followed by
/// the password page (#ap_password, "keep me signed in", #signInSubmit); some variants ask both on one page. A one-time
/// code page (#auth-mfa-otpcode, #auth-signin-button) gets the code of a stored TOTP secret with "don't require a code
/// on this browser" ticked, or the code is asked from the user. A captcha, an approval on another device and unknown
/// sign-in pages need the user: the waiting script reports them, so the page is shown (or a background run stops).
/// </summary>
internal static class AmazonSignIn
{
    /// <summary>The optional authenticator-app secret among the login's credentials.</summary>
    internal const string TotpKey = "totp";

    internal const string EmailSelector = "#ap_email";
    internal const string ContinueSelector = "#continue";
    internal const string PasswordSelector = "#ap_password";
    internal const string SignInSelector = "#signInSubmit";
    internal const string RememberMeSelector = "input[name=rememberMe]";
    internal const string CodePath = "/ap/mfa";
    internal const string CodeSelector = "#auth-mfa-otpcode";
    internal const string CodeSubmitSelector = "#auth-signin-button";
    internal const string RememberDeviceSelector = "#auth-mfa-remember-device";

    internal const string ErrorSelector = "#auth-error-message-box";
    internal static readonly string[] ErrorTexts =
        ["password is incorrect", "cannot find an account", "Passwort ist falsch", "Passwort ist nicht korrekt", "kein Konto mit dieser", "Kein Konto gefunden"];
    internal const string RejectedMessage = "Amazon rejected the e-mail address or password.";

    /// <summary>
    /// Run while the sign-in is not done: fine on the wallet, the sign-in form and the code page (the autofill and the
    /// code prompt handle those); a captcha, an approval request or another /ap/ page needs the user.
    /// </summary>
    internal const string WaitingScript =
        "() => { const shown = s => { const e = document.querySelector(s); return !!e && !!(e.offsetWidth || e.offsetHeight || e.getClientRects().length); };"
        + " const p = location.pathname;"
        + " if (shown('input[name=cvf_captcha_input], #auth-captcha-guess, #captchacharacters') || p.includes('validateCaptcha')) return { ok: false, reason: 'Amazon shows a captcha.' };"
        + " if (p.includes('/ap/cvf/approval') || shown('#resend-approval-link, #channelDetailsForApproval')) return { ok: false, reason: 'Amazon asks to approve the sign-in on another device.' };"
        + " if (shown('#auth-mfa-otpcode')) return { ok: false, reason: 'Amazon asks for a one-time code.' };"
        + " if (shown('#ap_email, #ap_password') || !p.startsWith('/ap/')) return { ok: true, reason: '' };"
        + " return { ok: false, reason: 'Amazon shows a sign-in page the app does not know.' }; }";

    internal static WebLoginAutofill? Autofill(ConnectorCredentials? credentials) =>
        credentials == null || string.IsNullOrEmpty(credentials.Username) || string.IsNullOrEmpty(credentials.Secret)
            ? null
            : new WebLoginAutofill(credentials.Username, credentials.Secret,
            [
                new WebLoginStep([
                    new WebLoginField(EmailSelector, WebLoginCredential.Username),
                    new WebLoginField(PasswordSelector, WebLoginCredential.Secret) { Optional = true },
                ], ContinueSelector + ", " + SignInSelector) { Tick = [RememberMeSelector] },
                new WebLoginStep(PasswordSelector, WebLoginCredential.Secret, SignInSelector) { SkipWhenEntered = true, Tick = [RememberMeSelector] },
            ]);

    internal static WebLoginCodePrompt CodePrompt(ConnectorCredentials? credentials) =>
        new(CodePath, CodeSelector, CodeSubmitSelector, "Enter the one-time code from Amazon (SMS) or your authenticator app.")
        {
            Totp = TotpSecret.TryParse(credentials?.ExtraValue(TotpKey), out var totp) ? totp : null,
            Tick = [RememberDeviceSelector],
        };

    internal static WebLoginError CredentialsError { get; } = new(ErrorSelector, ErrorTexts, RejectedMessage);
}
