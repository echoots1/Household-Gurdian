// Small helpers for the dashboard: JSON calls with the CSRF token, and the buttons that drive them. No framework.
(function () {
  const csrf = document.querySelector('meta[name=csrf]')?.content || '';
  const toast = (msg, ms = 2500) => { const t = document.getElementById('toast'); if (!t) return; t.textContent = msg; t.hidden = false; clearTimeout(t._h); t._h = setTimeout(() => t.hidden = true, ms); };
  async function call(method, url, body) {
    const r = await fetch(url, { method, headers: { 'Content-Type': 'application/json', 'RequestVerificationToken': csrf }, body: body === undefined ? undefined : JSON.stringify(body) });
    if (!r.ok) { let e = r.statusText; try { e = (await r.json()).error || e; } catch { } throw new Error(e); }
    const ct = r.headers.get('content-type') || ''; return ct.includes('json') ? r.json() : null;
  }
  const reload = () => location.reload();
  const on = (sel, ev, fn) => document.addEventListener(ev, e => { const el = e.target.closest(sel); if (el) fn(el, e); });

  // Live actions (Today, Alerts)
  on('[data-action]', 'click', async b => {
    const action = b.dataset.action; const body = {};
    if (b.dataset.prompt) { const v = prompt(b.dataset.prompt, b.dataset.default || ''); if (v === null) return; body[action === 'pause' || action === 'lock-now' ? 'hours' : 'minutes'] = parseInt(v, 10); }
    if (b.dataset.confirm && !confirm(b.dataset.confirm)) return;
    if (b.dataset.reason) body.reason = b.dataset.reason;
    try { await call('POST', '/api/v1/actions/' + action, body); toast('Done'); setTimeout(reload, 600); } catch (e) { toast('Failed: ' + e.message); }
  });
  on('[data-ack]', 'click', async b => { try { await call('POST', `/api/v1/alerts/${b.dataset.ack}/ack`); reload(); } catch (e) { toast(e.message); } });
  document.getElementById('ack-all')?.addEventListener('click', async () => { try { await call('POST', '/api/v1/alerts/ack-all'); reload(); } catch (e) { toast(e.message); } });
  on('select.reclass', 'change', async s => { if (!s.value) return; try { await call('POST', '/api/v1/rules/reclassify', { kind: s.dataset.kind, name: s.dataset.name, category: s.value }); toast('Rule added; history is being rebuilt'); setTimeout(reload, 800); } catch (e) { toast(e.message); } });
  on('[data-override]', 'click', async b => { try { await call('POST', '/api/v1/domains/override', { domain: b.dataset.domain, mode: b.dataset.override || null }); reload(); } catch (e) { toast(e.message); } });
  on('[data-del-exception]', 'click', async b => { if (!confirm('Delete this exception?')) return; try { await call('DELETE', '/api/v1/exceptions/' + b.dataset.delException); reload(); } catch (e) { toast(e.message); } });
  on('[data-custom-remove]', 'click', async b => { try { await call('POST', '/api/v1/lists/custom', { domain: b.dataset.customRemove, mode: 'remove' }); reload(); } catch (e) { toast(e.message); } });

  // Policy page
  document.getElementById('add-hours')?.addEventListener('click', () => { const t = document.querySelector('#hours .template'); const c = t.cloneNode(true); c.hidden = false; c.classList.remove('template'); t.before(c); });
  document.getElementById('save-policy')?.addEventListener('click', async () => {
    const f = document.getElementById('policy-form'); const v = n => f.querySelector(`[name=${n}]`)?.value || '';
    const win = (a, b) => v(a) && v(b) ? { start: v(a), end: v(b) } : null;
    const limits = {}; for (const k of ['total', 'Gaming', 'Other']) { const n = parseInt(v('limit_' + k), 10); if (n > 0) limits[k] = n; }
    const hours = {}; for (const row of f.querySelectorAll('#hours .hours-row:not(.template)')) {
      const g = n => row.querySelector(`[name=${n}]`).value; const cat = g('h_cat'); (hours[cat] ||= []).push({ days: g('h_days').split(',').map(s => s.trim()).filter(Boolean), start: g('h_start'), end: g('h_end') });
    }
    try { await call('PUT', '/api/v1/policy', { bedtime: { schoolNights: win('bed_school_start', 'bed_school_end'), weekends: win('bed_weekend_start', 'bed_weekend_end') }, dailyLimits: limits, categoryHours: hours, graceMinutes: 5 }); toast('Policy saved; applies within 15 s'); setTimeout(reload, 700); } catch (e) { toast('Not saved: ' + e.message, 5000); }
  });
  document.getElementById('add-exception')?.addEventListener('click', async () => {
    const f = document.getElementById('exception-form'); const v = n => f.querySelector(`[name=${n}]`)?.value || '';
    const body = { date: v('date'), noBedtime: f.querySelector('[name=noBedtime]').checked, note: v('note') || null, addMinutes: parseInt(v('addMinutes'), 10) || 0 };
    if (v('bedStart') && v('bedEnd')) body.bedtime = { start: v('bedStart'), end: v('bedEnd') };
    if (parseInt(v('total'), 10) > 0) body.dailyLimits = { total: parseInt(v('total'), 10) };
    try { await call('POST', '/api/v1/exceptions', body); reload(); } catch (e) { toast(e.message); }
  });

  // Settings page
  document.getElementById('save-settings')?.addEventListener('click', async () => {
    const f = document.getElementById('settings-form'); const values = {};
    for (const el of f.querySelectorAll('input[name]')) { if (el.name.startsWith('title_')) continue; values[el.name] = el.type === 'checkbox' ? (el.checked ? '1' : '0') : el.value; }
    values.title_categories = ['School', 'Gaming', 'Other'].filter(c => f.querySelector(`[name=title_${c}]`)?.checked).join(',');
    try { await call('PUT', '/api/v1/settings', { values }); toast('Saved'); setTimeout(reload, 600); } catch (e) { toast(e.message); }
  });
  document.getElementById('change-password')?.addEventListener('click', async () => {
    const f = document.getElementById('password-form'); try { await call('POST', '/api/v1/settings/password', { current: f.current.value, next: f.next.value }); toast('Password changed'); f.reset(); } catch (e) { toast(e.message, 4000); }
  });
  document.getElementById('change-user')?.addEventListener('click', async () => { const u = document.getElementById('monitored-user').value.trim(); if (!u || !confirm(`Monitor the account "${u}" from now on? The notice must be accepted again.`)) return; try { await call('POST', '/api/v1/settings/monitored-user', { domain: u }); reload(); } catch (e) { toast(e.message, 5000); } });
  document.getElementById('test-email')?.addEventListener('click', async () => { toast('Sending…'); try { await call('POST', '/api/v1/email/test'); toast('Test email sent'); } catch (e) { toast('Email failed: ' + e.message, 6000); } });
  document.getElementById('backup-now')?.addEventListener('click', async () => { toast('Backing up…'); try { const r = await call('POST', '/api/v1/backup/now'); toast(r.error ? 'Local copy written; share failed: ' + r.error : 'Backup written', 5000); } catch (e) { toast(e.message, 5000); } });
  document.getElementById('refresh-lists')?.addEventListener('click', async () => { toast('Downloading lists…', 10000); try { const r = await call('POST', '/api/v1/lists/refresh'); toast(Object.entries(r).map(([k, v]) => `${k}: ${v}`).join(' · '), 8000); setTimeout(reload, 3000); } catch (e) { toast(e.message, 5000); } });
  document.getElementById('add-custom')?.addEventListener('click', async () => { const d = document.getElementById('custom-domain').value.trim(); if (!d) return; try { await call('POST', '/api/v1/lists/custom', { domain: d }); reload(); } catch (e) { toast(e.message); } });
  document.getElementById('add-rule')?.addEventListener('click', () => { const t = document.querySelector('#rules .template'); const c = t.cloneNode(true); c.hidden = false; c.classList.remove('template'); t.before(c); });
  document.getElementById('save-rules')?.addEventListener('click', async () => {
    const rules = []; for (const row of document.querySelectorAll('#rules .rule-row:not(.template)')) { const g = n => row.querySelector(`[name=${n}]`).value; if (!g('r_pat').trim()) continue; rules.push({ priority: parseInt(g('r_pri'), 10) || 100, matchType: g('r_type'), pattern: g('r_pat').trim(), category: g('r_cat') }); }
    try { await call('PUT', '/api/v1/rules', rules); toast('Rules saved; rebuilding history'); setTimeout(reload, 800); } catch (e) { toast(e.message, 5000); }
  });

  // Child: request more time (loopback, no CSRF cookie involved)
  document.getElementById('send-request')?.addEventListener('click', async () => {
    const f = document.getElementById('time-request'); const out = document.getElementById('request-result');
    try { const r = await fetch('/me/time-request', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ reason: f.reason.value }) }); const j = await r.json(); out.textContent = j.message || 'Sent.'; f.reset(); } catch (e) { out.textContent = 'Could not send: ' + e.message; }
  });
})();
