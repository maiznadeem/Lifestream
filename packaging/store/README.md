# Microsoft Store submission (owner steps)

Lifestream ships first as a Velopack installer on GitHub Releases. This folder lets the same build also go to the Microsoft
Store as an MSIX. Nothing here is submitted automatically; the Store listing needs your Microsoft account.

## What is here

| File | Purpose |
|---|---|
| `AppxManifest.template.xml` | Full-trust desktop app manifest with placeholders for the Store identity. |
| `build-msix.ps1` | Lays out the published app, fills the manifest, adds the tile art from `Assets/` and runs `makeappx` to produce an unsigned `Lifestream-<version>-x64.msix`. |
| `Assets/` | The tile art: the Lifestream mark at 44x44, 150x150 and 50x50 (`Square44x44Logo.png`, `Square150x150Logo.png`, `StoreLogo.png`). |
| `screenshots/` | Created by `tools/generate-screenshots.ps1` (demo dataset only, at least 1366x768), with `screenshots.json` recording the dataset. |

## One-time setup (about 30 minutes)

1. Create a Microsoft Partner Center developer account (individual accounts are free) at <https://partner.microsoft.com/dashboard>.
2. In Partner Center choose **Apps and games > New product > MSIX or PWA app** and reserve the name **Lifestream**.
3. Open **Product management > Product identity** and copy three values:
   - `Package/Identity/Name` (for example `12345Example.Lifestream`)
   - `Package/Identity/Publisher` (for example `CN=00000000-0000-0000-0000-000000000000`)
   - `Package/Properties/PublisherDisplayName`
4. In the GitHub repository open **Settings > Secrets and variables > Actions > Variables** and add these three as
   repository **variables** (they are identifiers, not secrets): `STORE_IDENTITY_NAME`, `STORE_PUBLISHER`,
   `STORE_PUBLISHER_DISPLAY_NAME`. Until they exist, `release.yml` skips the MSIX and says so in the run summary. To build the
   package on your PC instead, set the same three as environment variables.
5. The tile art in `packaging/store/Assets/` is the Lifestream mark. To change it, replace `Square44x44Logo.png`, `Square150x150Logo.png` and
   `StoreLogo.png` there (44x44, 150x150 and 50x50). Only when one of them is missing does `build-msix.ps1` draw a placeholder (a red square with a white dot).

## Every release

1. Make the release with `tools/release-local.ps1` (see the README, Releases). It does not build the Store package.
2. Build the package, one of two ways:
   - On your PC, from the publish folder the release script left in its work folder (needs `makeappx` from the Windows SDK):
     `./packaging/store/build-msix.ps1 -PublishDir <work>/2.1.4/publish -Version 2.1.4 -OutputDir <folder>`, then scan it:
     `./tools/scan-artifacts.ps1 -Path <folder>`.
   - On GitHub, with the fallback workflow instead of the script: push the tag (`git tag v2.1.4 && git push origin v2.1.4`; a tag
     starts nothing by itself), start **Release** by hand on that tag (Actions > Release > Run workflow > Use workflow from: Tags),
     and download the artifact **store-msix** (`Lifestream-2.1.4-x64.msix`) from the run page. That run also makes the GitHub
     release, so use it only when the release was not already made with the script.
3. Screenshots: use the committed `packaging/store/screenshots/` (check `screenshots.json` and the images are from this
   release's look). To regenerate them, run `tools/generate-screenshots.ps1` on a machine with a local SQL Server (it needs
   one at `localhost`, which a GitHub-hosted runner does not have, so the **Screenshots** workflow runs only by hand) and commit the result.
4. In Partner Center open the reserved app, choose **Start your submission**, then:
   - **Packages**: upload the `.msix` (the Store signs it; no certificate is needed).
   - **Store listings**: upload the screenshots from `packaging/store/screenshots/` (at least one, 1366x768 or larger), add the description and
     a short privacy statement. The demo data is synthetic; say that the demo account is shared.
   - **Properties**: category *Health & fitness* or *Medical*; declare that the app uses the internet.
   - **Age ratings**: complete the questionnaire (no user-generated content, no purchases).
5. Submit for certification. Review usually takes one to three days. The Store updates the app itself, so the in-app
   Velopack update check must be off in the Store build (the client should skip it when running as a packaged app).

## Notes

- Full trust (`runFullTrust`) is required because the app is a regular WPF desktop app, not a UWP app.
- The Store package is a workflow artifact on purpose, never a GitHub release asset, so people downloading from GitHub get the
  Velopack installer with its auto-update feed.
- SmartScreen: the GitHub installer is unsigned unless the repository secrets `WINDOWS_SIGNING_PFX_BASE64` and
  `WINDOWS_SIGNING_PFX_PASSWORD` are set. The Store build is signed by Microsoft and does not show that warning.
