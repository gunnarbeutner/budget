# Chromium seccomp profile

`seccomp_profile.json` is the official Microsoft Playwright v1.64.1 profile:
https://github.com/microsoft/playwright/blob/v1.64.1/utils/docker/seccomp_profile.json

SHA-256: `cc3e61cabda6bbc1e53e54d27ba4d55a9d3be829b6dd1a596f4a7b31b1cc7849`.
The upstream project is Apache-2.0 licensed. The profile retains Docker's
default-deny model and allows `clone`, `setns` and `unshare` for Chromium's
non-root user-namespace sandbox. Apply it only to the browser container; it
needs neither `privileged` nor `SYS_ADMIN`.

The launcher probes for user namespaces at start and exits when they are
missing, so Chrome never runs with `--no-sandbox`.
