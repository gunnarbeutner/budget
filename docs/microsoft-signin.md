# Sign in with Microsoft

OpenBudget can offer **Sign in with Microsoft** (OpenID Connect, authorization code with PKCE, one Entra tenant)
next to password and passkey sign-in, which are unchanged. It reuses the Entra registration that already serves
OneDrive and receipts mail (`OPENBUDGET_ONEDRIVE_CLIENT_ID` / `OPENBUDGET_ONEDRIVE_CLIENT_SECRET`); the sign-in asks
only for `openid profile`. The mail permission (`Mail.Read`) is still asked for separately from the mailbox card.

A Microsoft account signs in only when it is **linked** to an OpenBudget user. A Microsoft sign-in never creates a
user and never links by e-mail address; an unlinked account sees "not linked" on the login page.

## Configuration

| Key (environment form) | Default | Purpose |
| --- | --- | --- |
| `Auth:Microsoft:Enabled` (`Auth__Microsoft__Enabled`) | `false` | Shows the Microsoft button and the linking section on the Account page. |
| `Auth:Microsoft:TenantId` (`Auth__Microsoft__TenantId`) | `OPENBUDGET_ONEDRIVE_TENANT` if that is a GUID | The only tenant accepted: `7d7a24f9-c6dc-4b10-b20d-413e1d4ce0d1`. |
| `OPENBUDGET_ONEDRIVE_CLIENT_ID`, `OPENBUDGET_ONEDRIVE_CLIENT_SECRET` | — | The existing registration `3b673f22-ba30-4dc9-affc-5f946fe10de8` and its secret (unchanged plumbing). |
| `Auth:Microsoft:Links:<object id>` (`Auth__Microsoft__Links__<object id>`) | — | Operator links: Entra object id → OpenBudget user name, in the configured tenant. Applied at every start; adds missing links, never moves an existing link to another user, logs (and skips) unknown user names. |

The app refuses to start when sign-in is enabled without tenant id, client id and secret, or when a link key is not
a GUID.

Example:

```bash
Auth__Microsoft__Enabled=true
Auth__Microsoft__TenantId=7d7a24f9-c6dc-4b10-b20d-413e1d4ce0d1
Auth__Microsoft__Links__<gunnar's object id>=gunnar
Auth__Microsoft__Links__<lisa's object id>=lisa
McpApi__Enabled=true
McpApi__Entra__Enabled=true
McpApi__AllowedClientId=c406b8b1-3f73-41cd-bf9b-e10e42514418
```

## Linking

- **User:** Account page → *Microsoft account* → **Link Microsoft account** runs the Microsoft sign-in and stores the
  account's tenant id and object id for the signed-in user; **Unlink** removes it. A Microsoft account already linked
  to another user is refused.
- **Operator:** `Auth:Microsoft:Links` (above).

Links live in the table `external_logins` (unique per tenant id + object id) and are removed with their user.

## Entra changes on registration 3b673f22-ba30-4dc9-affc-5f946fe10de8

1. **Authentication → Web → Redirect URIs:** add `https://budget.beutner.name/signin-microsoft` (keep
   `https://budget.beutner.name/connect/onedrive/callback`). No implicit-grant/ID-token checkbox is needed (code flow).
2. **Expose an API:** set the Application ID URI to `api://3b673f22-ba30-4dc9-affc-5f946fe10de8`; add the delegated
   scope `mcp.access` ("Read OpenBudget data through the MCP gateway", admins and users may consent).
3. Optionally under *Expose an API → Authorized client applications* add the MCP Gateway app
   `c406b8b1-3f73-41cd-bf9b-e10e42514418` for `mcp.access` (skips the consent prompt). The gateway app needs the
   API permission `api://3b673f22-…/mcp.access`.
4. **Token version:** optional. OpenBudget accepts v1 tokens (`aud` = `api://3b673f22-…`, `appid`) and v2 tokens
   (`aud` = the client id, `azp`); set `"accessTokenAcceptedVersion": 2` in the manifest for v2.
5. No optional claims are needed (`tid`, `oid`, `scp`, `azp`/`appid` are standard).
