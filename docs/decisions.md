# Implementation decisions and deviations from the spec

The spec (`docs/spec.md`) is the contract. These are the places where building it changed a detail, and why. Each one keeps the spec's principles (overt, minimal, predictable, local, one install).

| Spec says | Built as | Why |
| --- | --- | --- |
| The service samples the foreground window itself. | The tray (`GuardianTray.exe`, running in the child's session) takes the 5-second sample and sends it to the service over the pipe; the service stores, categorizes and enforces. | A Windows service lives in session 0 and cannot see the interactive desktop: `GetForegroundWindow`, `GetLastInputInfo` and window titles only work from inside the user's session. The tray is relaunched within 10 s if killed and every relaunch is recorded, exactly as the spec describes. |
| Tray is WPF with `Hardcodet.NotifyIcon.Wpf`. | WinForms `NotifyIcon`, no third-party package. | Fewer dependencies, native tray behaviour, same toolchain. |
| Child view at `http://localhost:47131/me`, extension update at `http://127.0.0.1:47131/ext/update.xml`. | Loopback HTTP on **47130** carries the child view, the extension listener, the extension update source and the tray's time-request call. **47131** is HTTPS for the parent; `/me` also answers there over loopback. | One port cannot serve plain HTTP to the child and HTTPS to the parent at once. Keeping the child on plain loopback HTTP means no certificate prompt for the child; the certificate is also placed in the machine's Trusted Root store so `https://localhost:47131` works on the PC too. |
| Forced-install extension from the local update URL. | Shipped as specified, **plus** an address-bar fallback: when no extension has reported for 2 minutes, the tray reads the active browser's address bar with UI Automation and reports the registrable domain and title. | Chrome and Edge only honour off-store `ExtensionInstallForcelist` entries on domain/Entra/MDM-joined PCs; on a home PC they are silently ignored. The fallback gives the same `{domain, title}` with the same never-the-path rule. To get real force-install on a home PC, publish the extension unlisted on the Chrome Web Store and put its store ID in the forcelist. |
| First-run wizard in the installer. | The MSI opens `http://localhost:47130/setup` in the admin's browser; the wizard is a loopback-only page that disappears once setup completes. | One UI instead of two; the same Razor pages, validation and password hashing as the dashboard. The monitored account can later be changed from Settings (this clears the notice acceptance). |
| Tray pipe is read-only. | The pipe carries samples from the tray to the service, plus notice acknowledgement and dismissals. The tray still cannot change policy or data. | The tray is the sensor (see row 1); the pipe is the only channel it has. |
| Full URL path is opt-in per category. | Not implemented: paths and query strings are never sent by the extension or the fallback. | Stricter is simpler, and nothing in v1 needs it. |
| Tray started by a per-user Run key. | HKLM `Run` key (starts for every account) **and** the service launches it in the monitored user's session if it is not reporting. | A per-user key cannot be written before the account has logged in once. |
| Optional TOTP in M4. | Not implemented. | Out of scope for v1; the single password with lockout and LAN-only exposure is the spec's minimum. |

Open questions from the spec, as decided:

- Monitor one named account (chosen in setup). Other accounts run the tray but nothing is recorded.
- Firefox: later. Firefox is still tracked as an app, and the address-bar fallback reads its window title.
- The parent's dashboard is reachable on loopback too (`https://localhost:47131`), so a parent can log in on the child's PC; `/me` remains for the child.
- Retention defaults: raw events 90 days, items 1 year, alerts and events 1 year; editable in Settings.
- The setup page and Settings show both the hostname URL and every LAN IP.
- Name: "Guardian" in the tray, service (`GuardianSvc`) and install folder.
