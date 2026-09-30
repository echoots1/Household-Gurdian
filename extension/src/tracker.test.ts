import { test } from "node:test";
import assert from "node:assert/strict";
import { registrableDomain, Tracker } from "./tracker";

test("registrableDomain: youtube", () => {
  assert.deepEqual(registrableDomain("https://www.youtube.com/watch?v=abc"), {
    domain: "youtube.com",
    host: "www.youtube.com",
  });
});

test("registrableDomain: multi-part public suffix", () => {
  assert.deepEqual(registrableDomain("https://news.bbc.co.uk/x"), { domain: "bbc.co.uk", host: "news.bbc.co.uk" });
});

test("registrableDomain: non-http schemes and localhost are null", () => {
  assert.equal(registrableDomain("chrome://extensions"), null);
  assert.equal(registrableDomain("edge://settings"), null);
  assert.equal(registrableDomain("about:blank"), null);
  assert.equal(registrableDomain("file:///C:/x.html"), null);
  assert.equal(registrableDomain("http://localhost:47130/me"), null);
  assert.equal(registrableDomain("not a url"), null);
  assert.equal(registrableDomain(""), null);
});

test("registrableDomain: IP addresses reported as-is", () => {
  assert.deepEqual(registrableDomain("http://192.168.1.10:8080/admin?x=1"), {
    domain: "192.168.1.10",
    host: "192.168.1.10",
  });
});

test("interval math: open at 0, close at 12000 -> 12 s", () => {
  const t = new Tracker("chrome");
  assert.equal(t.openInterval({ tabId: 1, url: "https://www.youtube.com/watch?v=abc", title: "Cats" }, 0), null);
  const rep = t.closeInterval(12_000);
  assert.deepEqual(rep, {
    domain: "youtube.com",
    subdomain: "www.youtube.com",
    title: "Cats",
    activeSeconds: 12,
    browser: "chrome",
    endedAt: "1970-01-01T00:00:12.000Z",
  });
  assert.equal(t.current, null);
  assert.equal(t.closeInterval(20_000), null);
});

test("flush splits the interval and restarts it", () => {
  const t = new Tracker("edge");
  t.openInterval({ tabId: 1, url: "https://example.com/a", title: "A" }, 0);
  const first = t.flush(30_000);
  assert.equal(first?.activeSeconds, 30);
  assert.equal(first?.subdomain, undefined);
  assert.equal(t.current?.startedAt, 30_000);
  const second = t.flush(45_000);
  assert.equal(second?.activeSeconds, 15);
  const last = t.closeInterval(50_000);
  assert.equal(last?.activeSeconds, 5);
  assert.equal(t.flush(60_000), null);
});

test("sub-second interval is skipped", () => {
  const t = new Tracker("chrome");
  t.openInterval({ tabId: 1, url: "https://example.com/", title: "A" }, 1000);
  assert.equal(t.closeInterval(1400), null);
  assert.equal(t.current === null, true);

  t.openInterval({ tabId: 1, url: "https://example.com/", title: "A" }, 0);
  assert.equal(t.flush(300), null);
  assert.equal(t.current?.startedAt, 0, "a skipped flush keeps the original start");
});

test("openInterval closes the previous interval and ignores unreportable tabs", () => {
  const t = new Tracker("chrome");
  t.openInterval({ tabId: 1, url: "https://a.example.org/", title: "A" }, 0);
  const closed = t.openInterval({ tabId: 2, url: "chrome://newtab", title: "New tab" }, 5000);
  assert.equal(closed?.domain, "example.org");
  assert.equal(t.current, null);
});

test("openInterval is a no-op for the same tab/host/title", () => {
  const t = new Tracker("chrome");
  t.openInterval({ tabId: 1, url: "https://example.com/a", title: "A" }, 0);
  assert.equal(t.openInterval({ tabId: 1, url: "https://example.com/b?q=1", title: "A" }, 4000), null);
  assert.equal(t.current?.startedAt, 0);
  const closed = t.openInterval({ tabId: 1, url: "https://example.com/b", title: "B" }, 9000);
  assert.equal(closed?.activeSeconds, 9);
  assert.equal(t.current?.title, "B");
});
