# Household Guardian — Software Spec

Sep 27, 2026 · @Evan Hoots

## Overview & design principles

Household Guardian is a self-hosted parental-control tool for one household, built as a single Windows service on the child's PC. That one service measures screen time by category, records web activity, flags unsafe content, enforces bedtime and time limits, and serves the web dashboard the parent and child both use. There is no server, no container, and nothing to install anywhere else.

It is overt by design. The child can see that the agent is running, what it collects, and their own activity summary. Enforcement is honest rather than covert: it relies on the child using a standard (non-admin) Windows account, not on anything being hidden. Monitoring an admin account is allowed; the child could then stop the service, and that is accepted.

**Principles**

- Transparent: a tray indicator is always visible while the service runs; a notice screen at setup explains exactly what is collected.
- Minimal collection: foreground app name, active domain, and time. Nothing more.
- Predictable enforcement: every limit is announced before it lands (5-minute and 1-minute warnings), and the schedule is visible to the child.
- Local and owned: all data stays on the child's PC and is read from a browser on the LAN. No cloud, no telemetry, no third-party accounts.
- One install: everything ships in one MSI; removing it removes everything.

**Never**

- No keylogging, screenshots, screen recording, clipboard capture, or mic/camera access.
- No reading of message content, email, or documents.
- No hidden processes, rootkit techniques, or disguised process names.
- No collection of full URLs by default; domain plus page title only (full path is opt-in per category, off by default).

## Scope

V1 is one Windows PC, one child, one parent, one service. Everything else is later or out.

**In scope (v1)**

- One Windows service that collects, stores, enforces, and hosts the web UI on a LAN port.
- A thin tray app for the visible indicator, warning notices, and a shortcut to the local web page.
- Screen-time analytics by category (School / Gaming / Other, parent-editable rules) with idle exclusion.
- Web activity history: domain, page title, active seconds, from a forced-install browser extension (Chrome and Edge).
- Content-safety flagging against domain category lists, with alerts in the dashboard and optional email.
- Scheduled policy: bedtime window, daily time limits (total and per category), allowed-category hours; enforcement by warning, app close, and sign-out.
- Parent dashboard and child "my activity" view served from the same process; single parent login, local accounts.
- Single MSI install, admin-only uninstall.

**Later**

- Multiple children or PCs, each running its own copy, with a read-only roll-up page.
- Per-app time limits and app approval on Windows.
- Push notifications to the parent (ntfy or similar).

**Out of scope**

- Any central server, container, or cloud component in v1.
- Content filtering or blocking of web traffic (DNS-level blocking stays on the gateway, outside this project).
- Multi-tenant, SSO, or role hierarchies beyond parent/child.
- Any form of message, keystroke, screen, or audio capture.

## Architecture

Everything runs on the child's PC. One Windows service does the work and hosts the web UI; a browser extension and a thin tray app feed and surface it. The parent needs only a browser on the same LAN.

&#91;embedded content: system architecture · one host, 3 processes\]

The service is the only process that touches data. The extension talks to it over loopback, the tray reads from it over a named pipe, and both browsers reach the same Kestrel host: the parent from the LAN with a login, the child from localhost without one.

**Data flow**

1. The service samples the foreground process every 5 s and receives tab activity from the extension over `127.0.0.1:47130`.
2. Samples are written straight to SQLite, categorized by the current rules, and rolled up per day per category every minute.
3. The scheduler runs every 15 s against the policy in SQLite: warnings, app close, sign-out. Policy edits on the dashboard take effect on the next tick.
4. Content-safety matching runs on each new domain and writes an alert; email goes out via SMTP if configured.
5. The web UI reads rollups and alerts from the same SQLite file; the tray reads state over the pipe and shows the icon and notices.
6. Optional: a nightly copy of the database to a NAS share for backup and off-PC reading. That is a file copy, not a service.

## Setup & trust

There is no pairing. Setup is the installer's first run: pick the account to monitor, set the parent password, and show the child the notice screen. Nothing needs to reach another machine.

**First run (admin session)**

1. Installer asks which local account is monitored. If the account is in the Administrators group it warns that the child could stop or remove the service, then continues.
2. Parent sets the dashboard password (12+ characters). The hash goes into SQLite; there is no username, just "parent".
3. Service generates a self-signed certificate for `https://<hostname>:47131`, valid 10 years, and shows its fingerprint plus a "download cert" link so the parent can trust it on their own devices.
4. Optional: SMTP settings for email alerts, and a backup path (`\\tank\guardian\` style UNC) for the nightly DB copy.
5. Service starts. Dashboard is live immediately at the URL shown; the wizard offers to open it.

**Notice screen (child's session)**

- Shown on the child's next login, full-screen, by the tray app. Setup is not complete, and enforcement does not start, until the child dismisses it with "I understand". The dashboard shows "waiting for notice acknowledgment" until then.
- Contents: who set this up and which account is monitored; what is collected (app names and time, website domains and titles, time of day); what is not (keystrokes, screenshots, camera, mic, messages, files); what can happen (warnings, apps closed, sign-out at bedtime or when a limit is reached); and where to see it (`http://localhost:47131/me`).
- The acceptance timestamp is stored and shown on both the child's page and the parent's Settings.
- The same text is always one click away in the tray menu under "What Guardian collects".

**Trust boundaries**

- Parent → dashboard: password over HTTPS on the LAN, session cookie.
- Child → own page: loopback only, no login. The `/me` routes are bound to `127.0.0.1` and never served on the LAN address.
- Extension → service: loopback only; the service rejects anything not from `127.0.0.1`.
- Tray → service: named pipe, read-only; the tray cannot change policy or data.

## Windows service & tray

One .NET 8 Worker Service process hosts five background workers and a Kestrel web server. A separate, deliberately thin tray app runs in the child's session. Splitting them is what makes enforcement work without hiding anything.

**Guardian service** (`GuardianSvc`, .NET 8, runs as LocalSystem, Automatic start, recovery = restart on failure)

- Sampler: every 5 s, `GetForegroundWindow` → `GetWindowThreadProcessId` → process name, executable path, window title, attributed to the interactive session's user. Only the monitored username is recorded.
- Idle: `GetLastInputInfo` per sample. Idle ≥ 120 s, a locked screen (`WTS_SESSION_LOCK`), or no interactive session marks samples `idle`; they never count toward a category.
- Categorizer + rollups: rules apply at write time; a rule change queues a rebuild of `rollup_daily` and `rollup_item` from raw events, so fixes apply to history.
- Scheduler + enforcer: 15 s tick against the policy in SQLite; see Policy & enforcement.
- Alerts: content-list matching on first sight of a domain; SMTP sender with a 5-minute digest so a burst of flags is one email.
- Extension listener: HTTP on `127.0.0.1:47130`, `POST /tab` with `{domain, title, activeSeconds, browser}`, loopback only.
- Web host: Kestrel on `0.0.0.0:47131` (HTTPS, self-signed cert) for the parent UI, plus `127.0.0.1:47131/me` for the child view. One process, two bindings; the `/me` routes check the connection's local address and 403 on anything but loopback.
- Named pipe `\\.\pipe\GuardianTray`: state, next warning, current notice text. Read-only.
- Storage: `%ProgramData%\Guardian\guardian.db` (SQLite, WAL). Folder ACL: SYSTEM and Administrators full control, nothing for Users. Nightly `VACUUM INTO` a dated copy in `backup\`, and to the UNC path if one is set.

**Tray app** (`GuardianTray.exe`, WPF, per-user Run key written by the installer for all users, so it starts for the monitored account)

- Always-visible tray icon with five states: on, warning (limit in 5 min), enforcing, waiting for notice, service down. No "offline" state exists anymore.
- Menu: Open my activity (launches `http://localhost:47131/me`), What Guardian collects (the notice text), Request more time (posts a `time_request` the parent sees as an alert).
- Shows the warning and countdown dialogs the enforcer asks for over the pipe. It has no other UI; every screen lives in the web UI.
- If the tray is killed, the service relaunches it in the user session within 10 s via `CreateProcessAsUser` and records a `tray_relaunch` event the parent can see. Not treated as an offense.

**Tamper posture (honest, not covert)**

- A standard user account for the child is the real control. The installer recommends it and warns on an admin account but does not require it; on an admin account the child can stop the service, and that is accepted.
- Service ACL denies stop/delete to non-admins (the LocalSystem default). Process names, install dir, and tray icon are plainly labeled "Guardian".
- If the service is not running, there is no dashboard. That absence is itself the tamper signal: the parent notices when `https://kidpc:47131` does not answer. Optional: the nightly backup job's absence on the NAS is a second, passive signal.

## Web activity

Web history comes from a browser extension the child can open and inspect, not from scraping browser databases. Everything outside the browser is covered by process sampling; there is no DNS feed.

**Browser extension** (Manifest V3, one codebase for Chrome and Edge)

- Forced-install and pinned via `ExtensionInstallForcelist` policy (HKLM keys written by the MSI). A standard user cannot disable or remove it, and the browser shows a "managed by your organization" badge, which is the overt signal.
- Firefox is not supported in v1; the notice text lists the supported browsers.
- Tracks the active tab only: on `tabs.onActivated`, `tabs.onUpdated`, `windows.onFocusChanged`, and `idle.onStateChanged`, it closes the current interval and opens a new one. Emits `{domain, title, activeSeconds, browser}` to `127.0.0.1:47130` when an interval ends or every 30 s while it is open.
- Domain = registrable domain (eTLD+1) via the public suffix list; subdomain is kept in the raw record. Path and query are never sent.
- Incognito/InPrivate is disabled by policy (`IncognitoModeAvailability = 1`) so there is no dark corner; the notice text says so.
- Popup shows the child what was sent in the last hour, and links to `localhost:47131/me`.
- Update source: the MSI ships the CRX and an `update.xml`; the forcelist entry points at `http://127.0.0.1:47131/ext/update.xml`, served by the service. No store, no external host.

**Content-safety flagging**

- On first sight of a domain, the service matches it against locally stored category lists: adult, gambling, drugs, weapons, plus a parent-editable custom list. Lists ship in the MSI and can be refreshed from the Settings page (the service downloads them; nothing else phones out).
- A match creates a `content_flag` alert with domain, category, first-seen time, and active seconds. Alerts show in the dashboard and go out by email digest if SMTP is set.
- Flagging is informational; the service never blocks. Blocking stays on the gateway.
- Parent can mark a domain "allowed" (suppress) or "always flag".

**Categorization rules** (apply to both app and web activity)

- Rule = `{matchType: process | domain | titleContains, pattern, category, priority}`. First match by priority wins; unmatched falls to Other.
- Shipped defaults: School (google.com, classroom, khanacademy, common LMS domains, Word/Excel/PowerPoint/OneNote), Gaming (steam.exe, epicgameslauncher.exe, roblox, minecraft, common game process names), Other (everything else). Parent edits in Settings.
- Rules are versioned; totals are rebuilt from raw events when rules change.

## Policy & enforcement

A policy is one JSON document in SQLite, versioned on every save, read by the scheduler on each 15 s tick. Edits on the dashboard apply on the next tick; there is no sync, cache, or offline mode because the enforcer and the policy live in the same process.

**Policy model**

```json
{
  "version": 42,
  "bedtime": { "schoolNights": {"start": "21:00", "end": "06:30"}, "weekends": {"start": "22:30", "end": "07:30"} },
  "dailyLimits": { "total": 240, "Gaming": 90, "Other": 120 },
  "categoryHours": { "Gaming": [{"days": ["Sat","Sun"], "start": "09:00", "end": "20:00"}] },
  "graceMinutes": 5,
  "exceptions": [ {"date": "2026-10-31", "bedtime": null, "dailyLimits": {"total": 360}} ]
}
```

- Minutes are per calendar day in the PC's local timezone; School time never counts toward the total limit.
- `categoryHours` restricts when a category may be used at all; outside those hours, apps in that category are closed after the warning sequence.
- `exceptions` are one-off overrides the parent adds from the dashboard ("extra hour today", "no bedtime tonight"). They apply within 15 s.

**Enforcement sequence** (same for bedtime, total limit, category limit, and category hours)

1. T−5 min: tray balloon and a small centered dialog: "Gaming time ends in 5 minutes." Dismissible.
2. T−1 min: second notice, not dismissible, with a countdown.
3. T+0: for category limits, `WM_CLOSE` to every window whose process is in that category, 15 s wait, then terminate. For bedtime and total limit, `WM_CLOSE` to all user-session processes, then a 15 s wait.
4. T+15 s (bedtime/total only): `WTSLogoffSession` on the child's session. Sign-out, not shutdown.
5. While the limit is in force, logging in shows the tray in "enforcing" state with the reason and lift time. The service logs the user off again 60 s after each login, after a visible notice, rate-limited to once per minute.
6. Every step is an `enforcement_event` with reason and policy version; both the parent's timeline and the child's page show it.

**Clock**

- Standard users cannot change the system time. The enforcer also refuses to move the day boundary backward if the clock jumps, and logs a `clock_jump` alert.

**Live actions from the dashboard**

- Add minutes today, pause all limits for N hours, lock now (1-minute warning then sign-out), cancel an in-progress enforcement. Each is an exception row with an audit entry the child can see on their Schedule tab.

## Web UI

One web app, served by the service, with two entrances: the parent's dashboard on the LAN behind a password, and the child's `/me` view on loopback with no login. Both render from the same rollup tables, so the child sees exactly the numbers the parent sees.

**Parent dashboard** (`https://<hostname>:47131`)

| Screen | Shows | Actions |
| --- | --- | --- |
| Today | Per-category time (School / Gaming / Other) as a stacked bar, remaining minutes against each limit, current app, service health | Add minutes, pause limits, lock now |
| Week | 7-day stacked bars by category, daily totals vs limits, bedtime compliance (sign-out at bedtime, later logins) | Pick a day to drill into Today for that date |
| Activity | Chronological timeline of app and site intervals with duration; filter by category, app, domain, date range; enforcement and tray events inline | Reclassify an item (creates a rule), mark a domain allowed or always-flag |
| Sites | Top domains by active time for the period, first-seen date, category, flag status | Same as Activity |
| Alerts | Content flags, time requests, tray relaunches, clock jumps, backup failures; unread badge in the nav | Acknowledge, allow domain, open in Activity |
| Policy | Bedtime by night type, daily limits, category hours, exceptions list, policy version | Edit and save (bumps version), add exception |
| Settings | Monitored account, notice acceptance time, category rules editor, list refresh, retention, SMTP, backup path, parent password, certificate download, export | Edit rules, export data, refresh lists |

**Child view** (`http://localhost:47131/me`, loopback only)

- Today: same stacked bar and limit meters as the parent's Today, top 5 apps and sites with minutes, current state. Auto-refreshes every 30 s.
- Schedule: this week's bedtime per night, daily limits, category hours, and any exceptions the parent added today ("+30 min added at 4:12 PM").
- What's collected: the notice text, the date it was accepted, and a plain list of exactly what fields are recorded, with the current retention windows.
- Request more time: one button and an optional one-line reason; lands as a `time_request` alert on the parent side.
- Nothing here changes anything. Requests go to the parent; the parent acts from the dashboard.

**Tray states and notices**

| State | Icon | Tooltip |
| --- | --- | --- |
| On | Shield, neutral | Guardian is on — click to see today |
| Warning | Shield, amber | Gaming ends in 4 min |
| Enforcing | Shield, red | Limit reached — lifts at 6:30 AM |
| Waiting for notice | Shield, outline | Please read the Guardian notice |
| Service down | Shield, grey, slashed | Guardian isn't running |

- Notices always state what, why, and when: "Bedtime in 5 minutes (school night, 9:00 PM). Save your work." One sentence, one button, no sound by default. The 1-minute countdown is the only non-dismissible UI in the product.

**Analytics rules**

- Active time = sum of non-idle sample intervals; 5 s cadence keeps totals within seconds per hour.
- App and web intervals never double-count: when the foreground process is a browser, the extension's domain interval wins and the browser process itself is not charged.
- Rollups are stored per day per category and rebuilt on rule changes; Today and Week never scan raw events.

**Alert delivery**

- In-app badge and Alerts screen always. Email via SMTP as a 5-minute digest if configured. No push in v1.

**Stack**

- ASP.NET Core minimal API + Razor Pages, htmx for partial refresh, uPlot for charts. Everything embedded in the service binary; no Node build, no CDN, no external requests from the page.

## Data model & API

One SQLite database (WAL mode) with raw events kept short-term and rollups kept long-term. The HTTP API exists only to serve the web UI, but it is versioned under `/api/v1` anyway.

**Tables**

| Table | Key columns | Notes |
| --- | --- | --- |
| `settings` | key, value | Monitored user, password hash (argon2id), notice\_accepted\_at, SMTP, backup path, retention, cert thumbprint |
| `policy` | id, version, json, saved\_at | Append-only; current = highest version |
| `exception` | id, date, json, created\_at, note | Merged into the effective policy at tick time |
| `activity_event` | id, started\_at, ended\_at, kind (app/web), process, title, domain, subdomain, idle, browser | Raw intervals; 90-day retention |
| `rollup_daily` | date, category, seconds, rules\_version | Rebuilt on rule change; kept forever |
| `rollup_item` | date, kind, name, category, seconds | Top apps/sites per day; kept 1 year |
| `rule` | id, match\_type, pattern, category, priority, created\_at | Ordered by priority |
| `category_list` | domain, list\_name, updated\_at | Shipped in the MSI; refreshed from Settings |
| `alert` | id, type, payload, created\_at, acknowledged\_at | content\_flag, time\_request, tray\_relaunch, clock\_jump, backup\_failed |
| `enforcement_event` | id, at, reason, step, policy\_version | Warned, closed, killed, logged\_off |

**Local endpoints** (loopback only)

- `POST :47130/tab` — extension → service, body `{domain, title, activeSeconds, browser}`
- `GET :47131/ext/update.xml`, `GET :47131/ext/guardian.crx` — extension install and update source for the browser policy
- `GET :47131/me`, `/me/schedule`, `/me/collected` — child pages; `POST /me/time-request`

**Parent API** (session cookie, SameSite=Strict, CSRF token on mutations)

- `POST /auth/login`, `POST /auth/logout`
- `GET /api/v1/today`, `GET /api/v1/week?start=`, `GET /api/v1/activity?from=&to=&category=&q=`, `GET /api/v1/sites?from=&to=`
- `GET /api/v1/policy`, `PUT /api/v1/policy` (bumps version), `POST /api/v1/exceptions`, `POST /api/v1/actions/{add-minutes|pause|lock-now|cancel}`
- `GET /api/v1/rules`, `PUT /api/v1/rules` (replaces set, queues rollup rebuild)
- `GET /api/v1/alerts?unread=1`, `POST /api/v1/alerts/{id}/ack`
- `GET /api/v1/settings`, `PUT /api/v1/settings`, `POST /api/v1/lists/refresh`, `GET /api/v1/export` (zip of CSVs), `GET /api/v1/cert` (the public cert for trusting on other devices)

**Retention**

- Raw `activity_event`: 90 days, nightly purge. Rollups: daily forever, items 1 year. Alerts and enforcement events: 1 year. All windows editable in Settings and shown on the child's "What's collected" page.

## Security & privacy

The data about a minor lives on the minor's own PC, so the controls are file ACLs, a standard user account, and a LAN-only HTTPS login. No exposure beyond the house.

**Accounts and transport**

- One parent password, argon2id, 12-character minimum, rate-limited (5 attempts, then 15-minute lockout). Optional TOTP in M4.
- Sessions: httpOnly, Secure, SameSite=Strict cookies, 12-hour idle timeout, CSRF token on every mutating request.
- Kestrel serves HTTPS on the LAN interface with a self-signed cert generated at first run; the parent trusts it once per device from the Settings download link. Child-facing routes and the extension listener bind to loopback only.
- Windows Firewall rule (written by the MSI) allows inbound 47131 from private-profile networks only. Never port-forward it; remote access goes through the existing VPN.

**On-disk protection**

- `%ProgramData%\Guardian\` ACL: SYSTEM and Administrators full control, Users nothing. A standard user cannot read the database, the cert key, or the SMTP password.
- Secrets in `settings` (SMTP password, backup share credential) are DPAPI-encrypted under the LocalSystem scope.
- Nightly backup copy inherits the destination share's permissions; the parent owns that share.

**What is stored, and what is not**

- Stored: process names, window titles, registrable domains and subdomains, page titles, timestamps, durations, idle flags, enforcement actions, service health.
- Not stored, by design and by code review: URL paths or query strings, keystrokes, screenshots, clipboard, file names, message bodies, audio, video, location.
- Window titles are the one field that can leak content (a document name, a video title). Title capture is switchable per category; School defaults to titles off.

**Retention and export**

- Raw events 90 days, rollups longer (see Data model). Export produces CSVs the parent can hand to the child. Uninstall offers "delete all data" as the default.

**Threat model**

- Defends against: the child stopping or removing the service when they run a standard account (admin-only service), disabling the extension (forced-install policy), incognito (policy off), quiet drift into unlimited use (local enforcement with no dependency on any other machine), a sibling reading the dashboard (login), the child reading the database (ACL).
- Does not defend against: a second device or a phone, a live-USB boot, another household's Wi-Fi, a child you chose to monitor on an admin account, or a determined teenager with physical access and an admin password. Those are parenting problems and the README says so.
- Third-party privacy: no telemetry, no crash reporting to outside services, no auto-update from anywhere except a file the parent downloads. The only outbound connections are SMTP (if set), the backup share (if set), and the category-list refresh (only when the parent clicks it).

## Deployment

One MSI on the child's PC. Nothing on TrueNAS, nothing on the parent's devices beyond trusting a certificate.

**Install (Windows 11, x64)**

1. Run `Guardian-<version>.msi` as admin. It creates `C:\Program Files\Guardian\`, registers `GuardianSvc`, writes the tray Run key for all users, writes the Edge and Chrome `ExtensionInstallForcelist` and `IncognitoModeAvailability` keys under HKLM, copies the CRX and category lists to `%ProgramData%\Guardian\`, sets the data-folder ACL, and adds the inbound firewall rule for 47131 (private profile).
2. First-run wizard in the admin session: monitored account (warns on admins), parent password, optional SMTP and backup path. Shows the dashboard URL and the cert fingerprint.
3. Sign in as the child once: the notice screen appears; the child taps "I understand". Enforcement starts.
4. On the parent's laptop or phone: open the URL, accept or install the cert, log in.

**Extension**

- Ships inside the MSI as a signed CRX. The forcelist entry points at `http://127.0.0.1:47131/ext/update.xml`, so the browser installs and updates it from the local service. No store listing, no external host.

**Backup**

- Nightly at 03:00 the service runs `VACUUM INTO` a dated copy under `backup\` (7 kept) and, if a UNC path is set, copies it there using the stored credential. A failed copy raises a `backup_failed` alert.
- The copy is a complete, read-only SQLite file; it can be opened from any machine to look at history when the PC is asleep.

**Updates**

- Parent downloads a new MSI and runs it over the top; the installer preserves the data folder and settings. The service applies migrations on start. No self-update mechanism in v1; the Settings page shows the installed version.

**Removal**

- Uninstall requires admin. It stops the service, removes the policy keys (the extension disappears on the next browser launch), removes the firewall rule, and asks whether to delete `%ProgramData%\Guardian\` (default yes).

## Tech stack & repo layout

One .NET 8 solution for everything on the PC, plus a small TypeScript extension. One language, one build, one installer.

| Part | Choice | Why |
| --- | --- | --- |
| Service | .NET 8 Worker Service hosting ASP.NET Core (Kestrel), P/Invoke for `GetForegroundWindow`, `GetLastInputInfo`, `WTSLogoffSession`, `CreateProcessAsUser` | First-class Windows service and web host in one process |
| Storage | `Microsoft.Data.Sqlite` + Dapper, WAL mode | No ORM ceremony, easy to inspect with any SQLite tool |
| Web UI | Razor Pages + htmx, uPlot for charts, embedded as static resources | No Node build, no CDN, works from a phone browser |
| Tray app | .NET 8 WPF, `Hardcodet.NotifyIcon.Wpf`, `System.IO.Pipes` client | Same toolchain as the service; shares a contracts project |
| Installer | WiX v4 | Service registration, policy registry keys, ACLs, firewall rule, per-machine install in one MSI |
| Extension | Manifest V3, TypeScript, esbuild, `psl` for eTLD+1 | Works in Chrome and Edge unchanged |
| Auth | ASP.NET Core cookie auth, `Konscious.Security.Cryptography` for argon2id | No identity framework needed for one user |
| CI | GitHub Actions on a Windows runner: build, test, MSI, extension CRX | A tag produces one MSI |

**Repository**

```
guardian/
  Guardian.sln
  src/
    Guardian.Contracts/   DTOs, pipe messages, policy model
    Guardian.Core/        sampler, categorizer, rollups, enforcer, alerts, storage
    Guardian.Service/     worker host + Kestrel web UI (Pages/, wwwroot/, Api/)
    Guardian.Tray/        WPF tray app
    Guardian.Installer/   WiX project, policy keys, ACL and firewall custom actions
  extension/              manifest.json, src/, build/
  lists/                  category lists shipped in the MSI, refresh script
  docs/                   this spec, notice-text.md, threat-model.md, README.md
```

- The notice text lives in `docs/notice-text.md` and is embedded into both the tray app and the web UI so the child's page and the tray menu always show the same words.
- Default rules and list sources live in `lists/defaults.yaml`, editable without a rebuild.

## Milestones

Build in the order the value arrives: measure first, enforce in the middle, polish last. M2 is the one that matters to the household; M0 and M1 exist to make it trustworthy, M3 makes it honest, M4 makes it installable.

&#91;embedded content: build order · 5 milestones, 5 gates\]

Each diamond is the acceptance test for that milestone; nothing moves to the next band until the gate passes on the real PC.

**Acceptance criteria**

- M0 Core: service installs by hand, samples land in SQLite within 5 s, idle is excluded, the web shell serves Today over HTTPS on the LAN behind the parent login, and the child's `/me` answers only on loopback.
- M1 Web: forced-install extension appears in Edge and Chrome from the local update URL, site minutes match app minutes without double counting, default rules put Steam in Gaming and Google Docs in School, one custom-list domain raises an alert.
- M2 Enforce: a 9:00 PM school-night bedtime produces the 5-min and 1-min notices and a sign-out at 9:00:15; a 90-minute Gaming limit closes Steam; an exception saved at 8:58 PM cancels bedtime within 15 s.
- M3 Child: notice screen blocks enforcement until accepted, acceptance time shows on both sides, `/me` matches the parent's Today to the minute, tray relaunches after being killed, "request more time" lands as an alert.
- M4 Ship: MSI installs on a clean Windows 11 VM and passes M0–M3 end to end, data folder is unreadable as the child, nightly backup lands on the NAS share, email digest arrives, export zip opens, README states the threat model, uninstall removes everything.

## Open questions & decisions

The single-service decision closed most of the earlier questions. What is left is small.

**Decisions made in this spec (change them here if you disagree)**

- Everything in one .NET 8 process on the child's PC; no server, no container. Trade: the dashboard is reachable only while the PC is on, and history lives on the kid's machine behind an ACL.
- Razor Pages + htmx for the web UI rather than a SPA. Trade: fewer moving parts, less interactivity; fine for a dashboard read on a phone.
- Self-signed HTTPS on the LAN port with a one-time trust step per parent device. Trade: a scary browser warning once, versus running a CA or exposing a public hostname.
- Nightly SQLite copy to a NAS share instead of any live replication. Trade: up to a day stale when the PC is asleep.
- Categorization at write time with rebuild on rule change, so fixes apply retroactively.
- Window titles on for Gaming and Other, off for School by default.
- No blocking. Flags only; DNS blocking stays on the gateway.

**Open**

- [ ] Monitor one named account, or every account on the PC?
- [ ] Firefox: needed in v1, or later?
- [ ] Should the parent's dashboard also be reachable on loopback for the child to look at with the parent present, or is `/me` enough?
- [ ] Retention: 90 days of raw events is a guess; is 30 enough?
- [ ] Is a hostname like `kidpc` stable on your LAN (UniFi DNS), or should the wizard show the IP too?
- [ ] Name. "Household Guardian" is a placeholder; pick something the kid won't hate seeing in the tray.
