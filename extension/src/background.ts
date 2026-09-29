// Guardian service worker: tracks the active tab and posts interval reports to the local service.
// Only network call: POST http://127.0.0.1:47130/tab (loopback). Nothing leaves this computer.

import { Tracker, type Interval, type TabReport } from "./tracker";

const ENDPOINT = "http://127.0.0.1:47130/tab";
const HEARTBEAT = "guardian-heartbeat";
const HEARTBEAT_MINUTES = 0.5; // MV3 minimum
const POST_TIMEOUT_MS = 2000;
const QUEUE_MAX = 200;
const LOG_MAX = 500;
const LOG_WINDOW_MS = 60 * 60 * 1000;

const BROWSER = navigator.userAgent.includes("Edg/") ? "edge" : "chrome";

export interface SentLogEntry {
  at: number;
  domain: string;
  title?: string;
  activeSeconds: number;
}

// chrome.storage.session exists from Chrome 102; fall back to local just in case.
const session: chrome.storage.StorageArea = chrome.storage.session ?? chrome.storage.local;

const tracker = new Tracker(BROWSER);

// Service workers are torn down when idle; keep the open interval in session storage so a
// restart (e.g. for the heartbeat alarm) can carry on with the same start time.
let hydrated: Promise<void> | null = null;
function hydrate(): Promise<void> {
  if (!hydrated) {
    hydrated = session.get("current").then((v) => {
      const cur = v["current"] as Interval | undefined;
      if (cur && typeof cur.startedAt === "number") tracker.restore(cur);
    });
  }
  return hydrated;
}
async function persist(): Promise<void> {
  await session.set({ current: tracker.current });
}

// Serialize event handling so overlapping async events cannot interleave tracker mutations.
let chain: Promise<void> = Promise.resolve();
function run(fn: () => Promise<void>): void {
  chain = chain.then(fn).catch((e) => console.warn("guardian:", e));
}

async function activeTab(): Promise<chrome.tabs.Tab | undefined> {
  const [tab] = await chrome.tabs.query({ active: true, lastFocusedWindow: true });
  return tab;
}

async function reopenFromActiveTab(now: number): Promise<void> {
  const tab = await activeTab();
  if (!tab || tab.id === undefined) {
    await emit(tracker.closeInterval(now));
    return;
  }
  await emit(tracker.openInterval({ tabId: tab.id, url: tab.url, title: tab.title }, now));
}

async function emit(rep: TabReport | null): Promise<void> {
  await persist();
  if (!rep) return;
  await send(rep);
}

// ---- transport ---------------------------------------------------------------------------

async function post(rep: TabReport): Promise<boolean> {
  const ctl = new AbortController();
  const timer = setTimeout(() => ctl.abort(), POST_TIMEOUT_MS);
  try {
    const res = await fetch(ENDPOINT, {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: JSON.stringify(rep),
      keepalive: true,
      signal: ctl.signal,
    });
    return res.ok;
  } catch {
    return false;
  } finally {
    clearTimeout(timer);
  }
}

async function send(rep: TabReport): Promise<void> {
  if (await post(rep)) {
    await recordSent([rep]);
  } else {
    await enqueue(rep);
  }
}

async function enqueue(rep: TabReport): Promise<void> {
  const v = await session.get("queue");
  const queue = (v["queue"] as TabReport[] | undefined) ?? [];
  queue.push(rep);
  while (queue.length > QUEUE_MAX) queue.shift();
  await session.set({ queue });
}

async function drainQueue(): Promise<void> {
  const v = await session.get("queue");
  const queue = (v["queue"] as TabReport[] | undefined) ?? [];
  if (queue.length === 0) return;
  const sent: TabReport[] = [];
  while (queue.length > 0) {
    const rep = queue[0]!;
    if (!(await post(rep))) break;
    queue.shift();
    sent.push(rep);
  }
  await session.set({ queue });
  await recordSent(sent);
}

async function recordSent(reps: TabReport[]): Promise<void> {
  if (reps.length === 0) return;
  const now = Date.now();
  const v = await chrome.storage.local.get("sentLog");
  const log = ((v["sentLog"] as SentLogEntry[] | undefined) ?? []).filter((e) => now - e.at <= LOG_WINDOW_MS);
  for (const r of reps) {
    const entry: SentLogEntry = { at: now, domain: r.domain, activeSeconds: r.activeSeconds };
    if (r.title) entry.title = r.title;
    log.push(entry);
  }
  while (log.length > LOG_MAX) log.shift();
  await chrome.storage.local.set({ sentLog: log, lastSentAt: now });
}

// ---- events ------------------------------------------------------------------------------

chrome.runtime.onInstalled.addListener(() => {
  chrome.alarms.create(HEARTBEAT, { periodInMinutes: HEARTBEAT_MINUTES });
});
chrome.runtime.onStartup.addListener(() => {
  chrome.alarms.create(HEARTBEAT, { periodInMinutes: HEARTBEAT_MINUTES });
});
// Also make sure the alarm exists whenever the worker wakes (covers reloads of an unpacked build).
chrome.alarms.get(HEARTBEAT).then((a) => {
  if (!a) chrome.alarms.create(HEARTBEAT, { periodInMinutes: HEARTBEAT_MINUTES });
});

chrome.tabs.onActivated.addListener(({ tabId }) => {
  run(async () => {
    await hydrate();
    const now = Date.now();
    let tab: chrome.tabs.Tab | undefined;
    try {
      tab = await chrome.tabs.get(tabId);
    } catch {
      /* tab gone */
    }
    if (!tab) {
      await emit(tracker.closeInterval(now));
      return;
    }
    await emit(tracker.openInterval({ tabId, url: tab.url, title: tab.title }, now));
  });
});

chrome.tabs.onUpdated.addListener((tabId, changeInfo, tab) => {
  if (changeInfo.url === undefined && changeInfo.title === undefined) return;
  if (!tab.active) return;
  run(async () => {
    await hydrate();
    // Only the tab in the focused window counts; a background window's active tab does not.
    const focused = await activeTab();
    if (!focused || focused.id !== tabId) return;
    await emit(tracker.openInterval({ tabId, url: tab.url, title: tab.title }, Date.now()));
  });
});

chrome.tabs.onRemoved.addListener((tabId) => {
  run(async () => {
    await hydrate();
    if (tracker.current?.tabId === tabId) await emit(tracker.closeInterval(Date.now()));
  });
});

chrome.windows.onFocusChanged.addListener((windowId) => {
  run(async () => {
    await hydrate();
    const now = Date.now();
    if (windowId === chrome.windows.WINDOW_ID_NONE) {
      await emit(tracker.closeInterval(now));
      return;
    }
    await reopenFromActiveTab(now);
  });
});

chrome.idle.setDetectionInterval(60);
chrome.idle.onStateChanged.addListener((state) => {
  run(async () => {
    await hydrate();
    const now = Date.now();
    if (state === "active") {
      await reopenFromActiveTab(now);
    } else {
      await emit(tracker.closeInterval(now));
    }
  });
});

chrome.alarms.onAlarm.addListener((alarm) => {
  if (alarm.name !== HEARTBEAT) return;
  run(async () => {
    await hydrate();
    await emit(tracker.flush(Date.now()));
    await drainQueue();
  });
});
