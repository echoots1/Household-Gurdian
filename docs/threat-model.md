# Threat model

Guardian is overt, local, and single-purpose. It is designed to be honest with the child and useful to the parent, not to win against a determined adversary with physical access.

## What it protects

- The parent's picture of screen time and web activity for one monitored account on one PC.
- Predictable enforcement of bedtime and time limits on that PC.
- The child's data: it never leaves the PC except to a share the parent owns (backup) or an SMTP server the parent configured (alert digests).

## Defends against

| Threat | Control |
| --- | --- |
| Child stops or removes the service | `GuardianSvc` runs as LocalSystem; a standard user cannot stop, disable, or uninstall it (MSI is per-machine, admin-only). |
| Child kills the tray / session agent | The service relaunches it within 10 s and records a `tray_relaunch` event the parent sees. Gaps in sampling are visible on the Activity page. Not treated as an offense. |
| Child disables the browser extension | Force-installed by policy (`ExtensionInstallForcelist`) where the PC honours it; otherwise the tray reads the address bar, which the child cannot disable from a standard account. |
| Incognito / InPrivate | Disabled by policy (`IncognitoModeAvailability` / `InPrivateModeAvailability` = 1). |
| Quiet drift into unlimited use | Enforcement is local: no dependency on any other machine, the network, or the cloud. |
| A sibling reading the dashboard | Parent password (argon2id), HTTPS on the LAN, 5 attempts then 15-minute lockout, `SameSite=Strict` cookie, CSRF token on every mutation. |
| Child reading the database, the certificate key, or the SMTP password | `%ProgramData%\Guardian` ACL: SYSTEM and Administrators only. Secrets are DPAPI-protected under the machine scope. |
| Clock tampering | Standard users cannot change the system time. If the clock jumps backwards anyway, the enforcer keeps the day boundary and raises a `clock_jump` alert. |
| Dashboard exposed beyond the house | Firewall rule allows inbound 47131 on private-profile networks only. Never port-forward it. |

## Does not defend against

- A second device or a phone.
- A live-USB boot or removing the disk.
- Another household's Wi-Fi (Guardian measures use of this PC, not the child's whole life).
- A child you chose to monitor on an administrator account: they can stop the service, and the installer warns you.
- A determined teenager with physical access and an admin password.

Those are parenting problems, and this document says so on purpose.

## What is never collected, by design and by code review

Keystrokes, screenshots, screen recording, clipboard, camera, microphone, message or email content, file contents or names, URL paths and query strings, location. Window titles are the one field that can leak content and are switchable per category (School is off by default).

## Outbound connections

Only three, all parent-configured: SMTP (if set), the backup share (if set), and the category-list download (only when the parent clicks "Refresh lists"). No telemetry, no crash reporting, no auto-update.
