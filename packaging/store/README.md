# Microsoft Store submission (owner steps)

Lifestream ships first as a Velopack installer on GitHub Releases. This folder lets the same build also go to the Microsoft
Store as an MSIX. Nothing here is submitted automatically; the Store listing needs your Microsoft account.

## What is here

| File | Purpose |
|---|---|
| `AppxManifest.template.xml` | Full-trust desktop app manifest with placeholders for the Store identity. |
| `build-msix.ps1` | Lays out the published app, fills the manifest, generates placeholder tile art and runs `makeappx` to produce an unsigned `Lifestream-<version>-x64.msix`. |
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
   `STORE_PUBLISHER_DISPLAY_NAME`. Until they exist, `release.yml` skips the MSIX and says so in the run summary.
5. Replace the generated tile art (`Assets/*.png`, red square with a white dot) with real artwork if you want: put your PNGs
   in `packaging/store/Assets/` named `Square44x44Logo.png`, `Square150x150Logo.png` and `StoreLogo.png` (44x44, 150x150 and 50x50); `build-msix.ps1` uses them instead of the generated ones.

## Every release

1. Push the tag (`git tag v2.0.0 && git push origin v2.0.0`). `release.yml` publishes the app, scans it, builds the GitHub release
   and, when the variables above exist, the workflow artifact **store-msix** (`Lifestream-2.0.0-x64.msix`).
2. Download the artifact from the workflow run page (Actions > Release > the run > Artifacts).
3. Generate screenshots: run the **Screenshots** workflow (Actions > Screenshots > Run workflow) and download its artifact.
4. In Partner Center open the reserved app, choose **Start your submission**, then:
   - **Packages**: upload the `.msix` (the Store signs it; no certificate is needed).
   - **Store listings**: upload the screenshots from the artifact (at least one, 1366x768 or larger), add the description and
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
