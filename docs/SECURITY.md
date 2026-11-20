# Security

## Reporting a vulnerability

Please report it privately, not in a public issue: on the repository's **Security** tab, choose **Report a vulnerability**. If that
button is not there, open an issue that asks only for a private contact (no details), and the maintainer will reply with one.

## Credentials in the git history

This repository is public, and its history holds two SQL Server logins from the 1.x course project (2023 and 2024). The history has
not been rewritten: that would not remove them from existing clones and forks. Neither login works:

- **2023** (commits 6354254, 7055838 and 871ab03; the last copy was removed in August 2024): a login for the server
  `database-server-bds`. That server was deleted, and its name no longer resolves in DNS.
- **2024** (commit 575d67c, removed the same day in 5b0d26e): a login for the server that was then called `lifestream-server`.
  That server was deleted as well; today's server of the same name was created in January 2025. The login is dead for two
  independent reasons:
  1. **The password was changed.** The server's SQL admin password was replaced on 2026-11-18 during the 2.0 overhaul (a server
     update at 18:56:57 UTC in the Azure activity log). The new value has never been in this repository.
  2. **No SQL login works at all.** The server accepts only Microsoft Entra sign-in (Entra-only authentication, on since
     2026-11-19; `infra/main.bicep` declares it and the deploy refuses to run without it), so no SQL user name and password is
     accepted, whatever the password.

Lifestream 2.x keeps no credential in the repository. The API reaches its database with its managed identity (no password), and
CI generates a throw-away SQL Server password for each run.

## How the repository is scanned

- Every pull request and every push to `master`: the tracked source and configuration, and the new commits
  (`.github/workflows/secret-scan.yml`, job `repository`).
- Every Monday and on a run started by hand: the whole history, every branch and tag (job `history`). The findings that were reviewed
  and proven harmless are listed in `.gitleaksignore`, each pinned to its commit with its reason, so any new finding fails the job.
- Built artifacts (the desktop app, the installer, the update packages and every layer of the API image) are scanned before they are
  uploaded or pushed.

GitHub's own secret scanning and push protection are switched on as well.
