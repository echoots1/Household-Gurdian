Guardian - what the installer did on this PC
=============================================

Guardian is an overt, local parental-control tool. Nothing it installs is hidden;
this file lists everything the MSI changed so an administrator can audit or undo it.

Program files (C:\Program Files\Guardian\)
  GuardianSvc.exe     Windows service "Guardian" (service name GuardianSvc). Runs as
                      LocalSystem, starts automatically, restarts on failure (3 times,
                      60 s apart, counter resets daily).
  GuardianTray.exe    Tray indicator + warning dialogs. Started for every account by an
                      HKLM Run key; the service only records the account chosen at setup.
  *.dll, runtime      Self-contained .NET 8 runtime shared by both programs. No separate
                      .NET install is needed.
  README-install.txt  This file.

Data folder (C:\ProgramData\Guardian\)
  guardian.db         SQLite database: activity, rollups, alerts, settings, policy.
  guardian.pfx        Self-signed HTTPS certificate created on first start.
  lists\              Category lists (adult, gambling, drugs, weapons, custom) plus
                      defaults.yaml and sources.txt. Never overwritten by upgrades.
  ext\                Packed browser extension (guardian.crx) and its id/version files,
                      served to the browsers at http://127.0.0.1:47130/ext/update.xml.
  backup\             Nightly database copies (7 kept).
  logs\               Service log.
  ACL: SYSTEM and Administrators full control; inheritance removed, so standard users
  (including the monitored account) cannot read anything in it. The service re-applies
  this ACL every time it starts.

Registry (HKEY_LOCAL_MACHINE)
  SOFTWARE\Policies\Google\Chrome\ExtensionInstallForcelist        1 = <id>;http://127.0.0.1:47130/ext/update.xml
  SOFTWARE\Policies\Google\Chrome\ExtensionInstallAllowlist        1 = <id>
  SOFTWARE\Policies\Google\Chrome                                  IncognitoModeAvailability = 1 (disabled)
  SOFTWARE\Policies\Microsoft\Edge\ExtensionInstallForcelist       1 = <id>;http://127.0.0.1:47130/ext/update.xml
  SOFTWARE\Policies\Microsoft\Edge\ExtensionInstallAllowlist       1 = <id>
  SOFTWARE\Policies\Microsoft\Edge                                 InPrivateModeAvailability = 1 (disabled)
  SOFTWARE\Microsoft\Windows\CurrentVersion\Run                    GuardianTray = "C:\Program Files\Guardian\GuardianTray.exe"
  SOFTWARE\Guardian                                                InstallDir, Version, ExtensionId (informational)
  <id> is the extension id shown in the Settings page. The browsers show a
  "managed by your organization" badge because of these keys; that is intended.
  Note: Chrome and Edge only force-install extensions from a non-store URL on a
  managed PC (domain / Entra ID / MDM). On a plain home PC the tray program's
  address-bar fallback provides web history instead.

Ports
  127.0.0.1:47130 (HTTP, loopback only)  extension reports, extension update source,
                                         child's own activity view, setup wizard.
  0.0.0.0:47131 (HTTPS, self-signed)     parent dashboard for browsers on the LAN.

Windows Firewall
  Inbound rule "Guardian dashboard (47131)": TCP 47131, program GuardianSvc.exe,
  private profile only. Never port-forward this port; use a VPN for remote access.

Uninstall (Settings > Apps, or msiexec /x)
  Requires an administrator. Stops and removes the service, removes the policy keys
  (the extension disappears on the next browser launch), the Run key and the
  firewall rule. Asks whether to delete C:\ProgramData\Guardian (default: yes).
  Silent uninstall keeps the data with:  msiexec /x Guardian-<version>.msi /qn DELETEDATA=0

Upgrade
  Run a newer MSI over the top. Program files are replaced; the data folder, the
  database, settings and edited lists are kept. The service migrates the database on
  start.
