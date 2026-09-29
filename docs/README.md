# Household Guardian

Self-hosted, overt parental controls for one Windows PC. One Windows service on the child's
computer measures screen time by category, records which websites are visited, flags unsafe
domains, enforces bedtime and daily limits, and serves the web dashboard that both the parent
and the child use. There is no server, no container, no cloud account and no telemetry.
Everything ships in one MSI; removing it removes everything.

The full design is in [`docs/spec.md`](docs/spec.md). The text the child sees is
[`docs/notice-text.md`](docs/notice-text.md).

## What it does

- **Screen time by category** (School / Gaming / Other, parent-editable rules). Foreground app
  sampled every 5 s; idle (2 min), locked screen and no-session time never count.
- **Web activity**: registrable domain, page title and active seconds from a Chrome/Edge extension
  the child can open and inspect. Path and query are never sent.
- **Content flags**: a domain seen for the first time is matched against local category lists
  (adult, gambling, drugs, weapons, custom). A match raises an alert, optionally emailed as a
  5-minute digest. Guardian never blocks anything; blocking stays on your gateway.
- **Policy**: bedtime window, daily total and per-category limits, allowed hours per category,
  with a 5-minute and a 1-minute warning before anything happens, then app close or sign-out.
- **Dashboard**: `https://<child-pc>:47131` for the parent (login, LAN only);
  `http://localhost:47131/me` for the child, showing the same numbers.
- **Tray icon**: always visible while the service runs, with "what Guardian collects" and
  "request more time" one click away.

## What it never does

- No keylogging, screenshots, screen recording, clipboard capture, or mic/camera access.
- No reading of message content, email, or documents.
- No hidden processes, rootkit techniques, or disguised process names. Everything is labelled
  "Guardian": the service, the folder, the tray icon, the browser badge.
- No full URLs (domain plus page title only), no cloud, no telemetry, no crash reports to anyone.
- The only outbound connections are SMTP (if you set it), your backup share (if you set it), and
  the category-list refresh (only when you click it).

## Threat model

Guardian is honest rather than covert. Its real control is that the child uses a **standard
(non-admin) Windows account**; the installer recommends it and warns otherwise.

**Defends against:** the child stopping or removing the service when they run a standard account
(admin-only service), disabling the extension (forced-install policy), incognito (policy off),
quiet drift into unlimited use (local enforcement with no dependency on any other machine), a
sibling reading the dashboard (login), the child reading the database (ACL).

**Does not defend against:** a second device or a phone, a live-USB boot, another household's
Wi-Fi, a child you chose to monitor on an admin account, or a determined teenager with physical
access and an admin password. Those are parenting problems and the README says so.

## Install

Requirements: Windows 10/11 x64 on the child's PC, an administrator account to run the installer,
and a browser on the parent's phone or laptop on the same network. The MSI is self-contained
(about 150 MB); no separate .NET install is needed.

1. **Run `Guardian-<version>.msi` as an administrator.** The first page is the notice text; read
   it, because it is what the child will read. The installer creates `C:\Program Files\Guardian\`,
   registers the `GuardianSvc` service, writes the tray Run key, writes the Chrome and Edge
   force-install / no-incognito policies under HKLM, copies the extension and category lists to
   `C:\ProgramData\Guardian\`, locks that folder down to SYSTEM and Administrators, and adds an
   inbound Windows Firewall rule for TCP 47131 on private networks. `README-install.txt` in the
   install folder lists every change.
2. **Setup page.** Leave "Open the setup page now" checked, or open
   `http://localhost:47130/setup` on the child's PC. Choose the monitored account (it warns if the
   account is an administrator), set the parent password (12+ characters), and optionally SMTP for
   alert emails and a backup share. The page shows the dashboard URL and the certificate
   fingerprint.
3. **Child accepts the notice.** Sign in as the child once. The notice screen appears, the child
   taps "I understand", and the acceptance time is recorded on both the parent's and the child's
   view. Enforcement does not start before that.
4. **Parent trusts the certificate.** On your own phone or laptop open the dashboard URL, accept
   the self-signed certificate (or download and install it from Settings, once per device), and
   log in. Never port-forward 47131; use your VPN for remote access.

### Updating

Download a newer MSI and run it over the top. Program files are replaced, the data folder,
settings and any lists you edited are kept, and the service migrates the database on start.
The Settings page shows the installed version.

## The browser extension caveat

The extension is what turns "Chrome was in front for 40 minutes" into "youtube.com, 25 minutes".
The MSI installs it the way managed PCs do: `ExtensionInstallForcelist` policy keys point at
`http://127.0.0.1:47130/ext/update.xml`, which the Guardian service serves along with the packed
CRX. The browser installs and pins it and shows a "managed by your organization" badge, which is
the intended, visible signal.

**Chrome and Edge only honor off-store force-install entries on a managed PC** (joined to an
Active Directory domain or Microsoft Entra ID, or enrolled in MDM). On a plain home PC the policy
is read but the entry is ignored. Guardian handles this in two ways:

- **Address-bar fallback (built in).** The tray program reads the address bar of the foreground
  browser window with UI Automation and reports the same domain/title/seconds with
  `browser: "tray"`. It is coarser (window caption instead of tab title, no per-tab idle handling)
  but keeps web history working with no extension at all. The service raises an
  `extension_missing` alert if a browser is in use and no extension reports arrive, so you know
  which mode you are in.
- **Publish the extension unlisted on the Chrome Web Store (optional).** Store-hosted extensions
  can be force-installed on any PC. Register a developer account, build `extension/`
  (`npm ci && npm run build`), zip the `build/` folder and upload it with visibility **Unlisted**.
  Note the store-assigned extension ID, then change both forcelist values to
  `<store-id>;https://clients2.google.com/service/update2/crx` (Edge accepts Chrome Web Store
  entries too, or use the Edge Add-ons store with `https://edge.microsoft.com/extensionwebstorebase/v1/crx`).
  The extension still only talks to `127.0.0.1:47130`; the store is just the delivery channel.

Whichever way it gets there, the popup shows the child exactly what was sent in the last hour.

## Where the MSI comes from

- **Releases:** every tag `v<x.y.z>` produces a GitHub Release with `Guardian-<x.y.z>.msi` and
  the packed `guardian.crx`.
- **CI artifacts:** every push runs `.github/workflows/build.yml`: tests on Linux, then the MSI
  on a Windows runner, uploaded as the `Guardian-msi` artifact (version `1.0.<run number>`).
- The extension ID is derived from the signing key. Set the repository secret `EXTENSION_KEY_PEM`
  (a PKCS#8 RSA-2048 private key; `tools/CrxPack` creates one) so the ID is the same on every
  build. Without it the ID changes per build and a new MSI means a new extension.

## Building from source

Linux, macOS or Windows for the service and tests; Windows for the MSI.

```
# .NET 8 SDK
dotnet test Guardian.sln -c Release          # Core, Service and CrxPack tests

# Node 22
cd extension && npm ci && npm run build && npm test && cd ..

# Pack the extension (creates key.pem if missing; keep it private and out of git)
dotnet run --project tools/CrxPack -- extension/build key.pem artifacts/ext/guardian.crx

# Windows only: service + tray + MSI (see src/Guardian.Installer/README.md)
dotnet publish src/Guardian.Service -c Release -r win-x64 --self-contained true -p:PublishTrimmed=false -o artifacts/publish
dotnet publish src/Guardian.Tray    -c Release -r win-x64 --self-contained true -p:PublishTrimmed=false -p:UseAppHost=true -o artifacts/publish
dotnet build src/Guardian.Installer/Guardian.Installer.wixproj -c Release -p:Version=1.0.0 `
  -p:PublishDir="$PWD\artifacts\publish" -p:ExtDir="$PWD\artifacts\ext" -p:ExtensionId=(Get-Content artifacts/ext/guardian.crx.id).Trim()
```

Repository layout:

```
Guardian.sln
src/Guardian.Contracts/   DTOs, pipe messages, policy model, names and ports
src/Guardian.Core/        sampler, categorizer, rollups, enforcer, alerts, storage
src/Guardian.Service/     GuardianSvc: worker host + Kestrel web UI
src/Guardian.Tray/        GuardianTray: tray icon, warnings, address-bar fallback
src/Guardian.Installer/   WiX v5 MSI (Windows-only build, not in the solution)
tools/CrxPack/            CRX3 packer used by CI and the Installer
extension/                Manifest V3 extension (TypeScript, esbuild)
lists/                    category lists shipped in the MSI
tests/                    xunit tests
docs/                     spec, notice text, this README
```

## Uninstall

Settings > Apps > Guardian > Uninstall (administrator required), or
`msiexec /x Guardian-<version>.msi`. It stops and removes the service, removes the policy keys
(the extension disappears on the next browser launch), the tray Run key and the firewall rule,
and asks whether to delete `C:\ProgramData\Guardian` (activity history, settings, backups,
certificate). The default is **yes**. For a silent uninstall that keeps the data:
`msiexec /x Guardian-<version>.msi /qn DELETEDATA=0`. Copies on a backup share are never touched.

## Data and privacy

All data about the child lives on the child's PC in `C:\ProgramData\Guardian`, readable only by
administrators and the system. Raw events are kept 90 days, daily totals longer. The parent can
export CSVs from the dashboard and hand them to the child. Nightly the service copies the
database to `backup\` (7 kept) and, if configured, to a share you own.

## License

See the repository license file. Category lists are seeded from MIT-licensed public sources
listed in `lists/sources.txt`.
