// Popup: shows the child what this extension sent in the last hour and whether the service answers.

interface SentLogEntry {
  at: number;
  domain: string;
  title?: string;
  activeSeconds: number;
}

const LOG_WINDOW_MS = 60 * 60 * 1000;
const STALE_MS = 2 * 60 * 1000; // heartbeat is 30 s; no success for 2 min means the service is not answering

const $ = <T extends HTMLElement>(id: string): T => document.getElementById(id) as T;

function fmtTime(ms: number): string {
  return new Date(ms).toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" });
}

function fmtAgo(ms: number): string {
  const s = Math.max(0, Math.round((Date.now() - ms) / 1000));
  if (s < 60) return `${s} s ago`;
  const m = Math.round(s / 60);
  if (m < 60) return `${m} min ago`;
  return fmtTime(ms);
}

async function load(): Promise<void> {
  const local = await chrome.storage.local.get(["sentLog", "lastSentAt"]);
  const session: chrome.storage.StorageArea = chrome.storage.session ?? chrome.storage.local;
  let queueLen = 0;
  try {
    const s = await session.get("queue");
    queueLen = ((s["queue"] as unknown[] | undefined) ?? []).length;
  } catch {
    /* session storage may be unavailable to the popup on old builds */
  }

  const lastSentAt = local["lastSentAt"] as number | undefined;
  const status = $("status");
  const lastSent = $("last-sent");
  if (lastSentAt) {
    lastSent.textContent = fmtAgo(lastSentAt);
    const fresh = Date.now() - lastSentAt < STALE_MS;
    status.textContent = fresh ? "connected" : "not answering";
    status.className = fresh ? "ok" : "bad";
  } else {
    lastSent.textContent = "never";
    status.textContent = "no contact yet";
    status.className = "muted";
  }
  $("queue").textContent = String(queueLen);

  const now = Date.now();
  const log = ((local["sentLog"] as SentLogEntry[] | undefined) ?? [])
    .filter((e) => now - e.at <= LOG_WINDOW_MS)
    .reverse();

  const tbody = $("log").querySelector("tbody")!;
  tbody.textContent = "";
  for (const e of log) {
    const tr = document.createElement("tr");
    const t = document.createElement("td");
    t.textContent = fmtTime(e.at);
    const site = document.createElement("td");
    site.className = "site";
    site.textContent = e.domain;
    if (e.title) site.title = e.title;
    const secs = document.createElement("td");
    secs.className = "num";
    secs.textContent = String(e.activeSeconds);
    tr.append(t, site, secs);
    tbody.appendChild(tr);
  }
  $("log").hidden = log.length === 0;
  $("empty").hidden = log.length > 0;
}

$("see-activity").addEventListener("click", (ev) => {
  ev.preventDefault();
  void chrome.tabs.create({ url: "http://localhost:47130/me" });
});

void load();
