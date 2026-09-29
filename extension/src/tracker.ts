// Pure, testable interval tracker for the active browser tab.
// No chrome.* APIs in here; background.ts wires events to it.

import psl from "psl";

/** Exact JSON the Guardian service accepts at POST /tab (see src/Guardian.Contracts/Web.cs). */
export interface TabReport {
  domain: string;
  subdomain?: string;
  title?: string;
  activeSeconds: number;
  browser: string;
  endedAt?: string;
}

export interface Interval {
  tabId: number;
  host: string;
  domain: string;
  title: string;
  startedAt: number;
}

export interface TabInfo {
  tabId: number;
  url?: string | undefined;
  title?: string | undefined;
}

const IPV4 = /^\d{1,3}(\.\d{1,3}){3}$/;

/**
 * Registrable domain (eTLD+1) plus full host for a URL. Returns null for anything that
 * should never be reported: non-http(s) schemes (chrome://, about:, file:, edge://, ...),
 * localhost, and unparseable hosts. Bare IP addresses are reported as-is.
 * Path, query and fragment are never part of the result.
 */
export function registrableDomain(url: string): { domain: string; host: string } | null {
  let parsed: URL;
  try {
    parsed = new URL(url);
  } catch {
    return null;
  }
  if (parsed.protocol !== "http:" && parsed.protocol !== "https:") return null;

  const host = parsed.hostname.toLowerCase().replace(/\.$/, "");
  if (!host) return null;
  if (host === "localhost" || host.endsWith(".localhost")) return null;

  // IPv6 literal (URL.hostname keeps the brackets) or IPv4 -> report the address itself.
  if (host.startsWith("[") || IPV4.test(host)) return { domain: host, host };

  const domain = psl.get(host);
  if (!domain) return null;
  return { domain, host };
}

export class Tracker {
  current: Interval | null = null;

  constructor(private readonly browser: string) {}

  /** Restore state persisted by the service worker across restarts. */
  restore(interval: Interval | null): void {
    this.current = interval;
  }

  /**
   * Close the open interval (if any) and, when the tab is reportable, open a new one at `now`.
   * If the tab describes the interval that is already open (same tab, host and title) nothing changes.
   * Returns the report for the closed interval, or null.
   */
  openInterval(tab: TabInfo, now: number): TabReport | null {
    const parsed = tab.url ? registrableDomain(tab.url) : null;
    const title = (tab.title ?? "").trim();

    if (
      parsed &&
      this.current &&
      this.current.tabId === tab.tabId &&
      this.current.host === parsed.host &&
      this.current.title === title
    ) {
      return null;
    }

    const closed = this.closeInterval(now);
    if (parsed) {
      this.current = { tabId: tab.tabId, host: parsed.host, domain: parsed.domain, title, startedAt: now };
    }
    return closed;
  }

  /**
   * End the open interval at `now` and clear state. Returns the report, or null when there was
   * no interval or it lasted under one second (those are skipped, not sent).
   */
  closeInterval(now: number): TabReport | null {
    const cur = this.current;
    this.current = null;
    if (!cur) return null;
    return this.report(cur, now);
  }

  /**
   * Heartbeat: emit a report for the elapsed part of the open interval and restart it at `now`,
   * so the service never sees an interval longer than the heartbeat period.
   */
  flush(now: number): TabReport | null {
    const cur = this.current;
    if (!cur) return null;
    const rep = this.report(cur, now);
    if (rep) this.current = { ...cur, startedAt: now };
    return rep;
  }

  private report(cur: Interval, now: number): TabReport | null {
    const activeSeconds = Math.round((now - cur.startedAt) / 1000);
    if (activeSeconds < 1) return null;
    const rep: TabReport = {
      domain: cur.domain,
      activeSeconds,
      browser: this.browser,
      endedAt: new Date(now).toISOString(),
    };
    if (cur.host !== cur.domain) rep.subdomain = cur.host;
    if (cur.title) rep.title = cur.title;
    return rep;
  }
}
