# Guardian.Installer

WiX v5 (SDK-style) project that produces `Guardian-<version>.msi`. It is **not** in `Guardian.sln`
because WiX only builds on Windows; CI (`.github/workflows/build.yml`) builds it on a Windows runner
and the same commands work on a Windows dev box.

Requirements on Windows: .NET 8 SDK, Node 22 (for the extension). The WiX toolset itself comes from
the `WixToolset.Sdk/5.0.2` package reference; no `wix` CLI install and no EULA is needed.

## Build locally

From the repository root, in PowerShell:

```powershell
# 1. Extension -> build/  -> packed CRX (creates extension-key.pem on first run; keep it and never commit it)
cd extension; npm ci; npm run build; cd ..
dotnet run --project tools/CrxPack -- extension/build extension-key.pem artifacts/ext/guardian.crx
$extId = (Get-Content artifacts/ext/guardian.crx.id).Trim()

# 2. Service + tray, self-contained, merged into ONE folder (shared runtime files are identical)
dotnet publish src/Guardian.Service -c Release -r win-x64 --self-contained true -p:PublishTrimmed=false -o artifacts/publish
dotnet publish src/Guardian.Tray    -c Release -r win-x64 --self-contained true -p:PublishTrimmed=false -p:UseAppHost=true -o artifacts/publish

# 3. MSI
dotnet build src/Guardian.Installer/Guardian.Installer.wixproj -c Release `
  -p:Version=1.0.0 `
  -p:PublishDir="$PWD\artifacts\publish" `
  -p:ExtDir="$PWD\artifacts\ext" `
  -p:ExtensionId=$extId
# -> src/Guardian.Installer/bin/x64/Release/Guardian-1.0.0.msi  (about 150 MB: it carries the .NET 8 runtime)
```

Pass absolute paths for `PublishDir` and `ExtDir`. The defaults are `<repo>\artifacts\publish` and
`<repo>\artifacts\ext`, so if you used the commands above verbatim you can omit them.

| MSBuild property | Meaning | Default |
| --- | --- | --- |
| `Version` | MSI ProductVersion, `x.y.z` | `1.0.0` |
| `PublishDir` | merged publish folder containing `GuardianSvc.exe`, `GuardianTray.exe` and the runtime | `<repo>\artifacts\publish` |
| `ExtDir` | folder with `guardian.crx`, `guardian.crx.id`, `guardian.crx.version` from CrxPack | `<repo>\artifacts\ext` |
| `ExtensionId` | 32-char `a`..`p` id printed by CrxPack (`ID=`) | placeholder `aaaa…` (build warns) |
| `ListsDir` | source of the shipped category lists | `<repo>\lists` |

The build fails early if `PublishDir` lacks either exe or `ExtDir` lacks the CRX.

## What the MSI does

See `README-install.txt` (also installed next to the exes). In short: service `GuardianSvc`
(LocalSystem, auto start, restart on failure), tray Run key (HKLM), Chrome/Edge force-install +
allowlist + no-incognito policies, `%ProgramData%\Guardian` with lists and the packed extension, an
ACL of SYSTEM + Administrators only, an inbound firewall rule for TCP 47131 on private networks,
and on uninstall an optional (default on) deletion of the data folder.

## Files

| File | Content |
| --- | --- |
| `Package.wxs` | `Package`, `MajorUpgrade`, launch condition, properties, feature, custom actions (ACL via `icacls`, data deletion, open setup page), UI (WixUI_InstallDir + `DeleteDataDlg`) |
| `Folders.wxs` | `INSTALLFOLDER`, harvested app files (`<Files Include="$(var.PublishDir)\**">`), service component with `ServiceInstall`/`ServiceControl`/`util:ServiceConfig` and `fw:FirewallException` |
| `Registry.wxs` | policy keys, Run key, `HKLM\SOFTWARE\Guardian` info |
| `Data.wxs` | `CommonAppDataFolder\Guardian` tree, `util:PermissionEx`, list files (`NeverOverwrite`), extension files |
| `license.rtf` | shown as the "license" page: the notice text (what Guardian collects) |
| `README-install.txt` | installed audit sheet |

## Silent install / uninstall

```
msiexec /i Guardian-1.0.0.msi /qn
msiexec /x Guardian-1.0.0.msi /qn                # deletes C:\ProgramData\Guardian
msiexec /x Guardian-1.0.0.msi /qn DELETEDATA=0   # keeps it
```

## Testing checklist (clean Windows 11 x64 VM)

1. Install; `sc query GuardianSvc` running; `http://localhost:47130/setup` opens.
2. `icacls C:\ProgramData\Guardian` shows only SYSTEM and Administrators, no inherited entries.
3. `chrome://policy` lists `ExtensionInstallForcelist` with the id from `guardian.crx.id`.
4. `Get-NetFirewallRule -DisplayName "Guardian dashboard (47131)"` exists, profile Private.
5. Edit `C:\ProgramData\Guardian\lists\custom.txt`, install a higher-version MSI over the top: edit survives, service restarts.
6. Uninstall with the box checked: service, keys, rule and data folder are gone.
