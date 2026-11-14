import { languages, resolveLanguage, setLanguage, t, number, locale, LANGUAGE_KEY } from './i18n.js';
import { summarize, activityDays, parseCount, matchesCsv } from './stats.js';
import { RATES, WEEK_STARTS, nextTreasureHour, summarizeHunt, luckRows, plan, formatChance, formatLeft } from './treasure.js';

const CREATOR_CODE = 'BRNYT';
const $ = id => document.getElementById(id);
const el = (tag, text, className) => { const node = document.createElement(tag); if (text != null) node.textContent = text; if (className) node.className = className; return node; };

let snapshot = { total: 0, matches: [], accounts: [] };
let hunt = { matches: [], liveChests: 0 };
let online = false, polling = false, page = 1, settingsLoaded = false, zoom = 100, toastTimer;
let overlay = { showLabel: false, label: '', showLastMatch: false }, overlayDirty = false, overlayTimer;
let huntKey = '';

async function api(path, options = {}) {
  const response = await fetch(path, { cache: 'no-store', signal: AbortSignal.timeout(4000), ...options });
  if (!response.ok) throw new Error(`HTTP ${response.status}`);
  return response.json();
}
function toast(key) {
  $('toast').textContent = t(key); $('toast').hidden = false;
  clearTimeout(toastTimer); toastTimer = setTimeout(() => { $('toast').hidden = true; }, 3000);
}
async function copy(text) {
  try { await navigator.clipboard.writeText(text); toast('copied'); } catch { toast('error'); }
}
const overlayUrl = () => location.origin + '/overlay';

// ---- Dashboard ------------------------------------------------------------------------------
function displayMode(playlist) {
  if (!playlist) return '—';
  const mode = playlist.toLowerCase();
  if (!/default|nobuild|showdown|battle royale|zero build|solo|duo|trio|squad/.test(mode)) return playlist.replace(/^Playlist_/, '').replaceAll('_', ' ');
  const parts = [];
  if (/showdown|ranked/.test(mode)) parts.push(t('modeRanked'));
  parts.push(t(/nobuild|zero.?build/.test(mode) ? 'modeZB' : 'modeBR'));
  for (const [pattern, key] of [[/solo/, 'modeSolo'], [/duo/, 'modeDuo'], [/trio/, 'modeTrio'], [/squad/, 'modeSquad']]) if (pattern.test(mode)) parts.push(t(key));
  return parts.join(' · ');
}
function renderDashboard() {
  const values = summarize(snapshot), matches = snapshot.matches || [];
  for (const key of ['total', 'session', 'lastMatch', 'average', 'victories']) $(key).textContent = number(values[key]);
  $('best').textContent = number(Math.max(0, ...matches.map(m => Number(m.chestsOpened) || 0)));
  const seconds = matches.reduce((n, m) => n + (Number(m.durationSeconds) || 0), 0);
  const chests = matches.reduce((n, m) => n + (Number(m.chestsOpened) || 0), 0);
  $('perMinute').textContent = seconds ? number(chests / (seconds / 60), 2) : '0';
  $('totalHint').textContent = snapshot.base > 0 ? `${t('base')}: ${number(snapshot.base)}` : t('fromReplays');
  $('matchStatus').hidden = !snapshot.inProgress;
  $('player').textContent = snapshot.player || t('noProfile');
  $('statusText').textContent = t(online ? 'connected' : snapshot.player ? 'offline' : 'waiting');
  $('status').classList.toggle('online', online);

  const accounts = snapshot.accounts?.length ? snapshot.accounts : [];
  const signature = JSON.stringify(accounts);
  if ($('profile').dataset.signature !== signature) {
    $('profile').replaceChildren(...accounts.map(account => {
      const option = el('option', `${account.name || account.key} · ${number(account.chests || 0)}`);
      option.value = account.key; option.selected = !!account.active;
      return option;
    }));
    $('profile').dataset.signature = signature;
  }
  $('profile').hidden = accounts.length < 2;
  if (document.activeElement !== $('base')) $('base').value = snapshot.base ?? '';
  renderChart(); renderMatches(); renderOverlayPreview();
}
function renderChart() {
  const days = activityDays(snapshot.matches || []), max = Math.max(1, ...days.map(day => day.count));
  const fmt = new Intl.DateTimeFormat(locale, { month: 'short', day: 'numeric' });
  $('chart').replaceChildren(...days.map(day => {
    const bar = el('div', null, 'bar' + (day.count ? '' : ' zero'));
    bar.style.height = day.count ? `${Math.max(5, day.count / max * 100)}%` : '3px';
    bar.title = `${fmt.format(day.date)} · ${number(day.count)} ${t('chests')}`;
    return bar;
  }));
  $('chartStart').textContent = fmt.format(days[0].date); $('chartEnd').textContent = fmt.format(days[29].date);
  $('chartEmpty').hidden = days.some(day => day.count > 0);
  $('chart').setAttribute('aria-label', `${t('activity')} · ${t('last30')}`);
}
function renderMatches() {
  const matches = [...(snapshot.matches || [])].sort((a, b) => new Date(b.matchStart) - new Date(a.matchStart));
  const pages = Math.max(1, Math.ceil(matches.length / 10)); page = Math.min(page, pages);
  const fmt = new Intl.DateTimeFormat(locale, { month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit' });
  const rows = matches.slice((page - 1) * 10, page * 10).map(match => {
    const row = el('tr'), date = new Date(match.matchStart);
    row.append(
      el('td', Number.isNaN(date.getTime()) ? '—' : fmt.format(date)),
      el('td', displayMode(match.playlist)),
      el('td', match.placement ? `#${number(match.placement)}` : '—', match.placement === 1 ? 'win' : ''),
      el('td', number(match.chestsOpened), 'num count'),
      el('td', match.eliminations == null ? '—' : number(match.eliminations), 'num'));
    return row;
  });
  if (!rows.length) {
    const row = el('tr'), cell = el('td', t('noMatches'), 'empty'); cell.colSpan = 5; row.append(cell); rows.push(row);
  }
  $('matchRows').replaceChildren(...rows);
  $('pagination').hidden = matches.length <= 10; $('export').disabled = !matches.length;
  $('pageLabel').textContent = t('page', { current: number(page), total: number(pages) });
  $('previous').disabled = page <= 1; $('next').disabled = page >= pages;
}

// ---- Fights and loot (from the end-of-match stats and the chest items) ---------------------------
const RARITIES = ['common', 'uncommon', 'rare', 'epic', 'legendary', 'mythic'];
function renderInsights(matches) {
  const withStats = matches.filter(m => m.stats);
  const avg = pick => withStats.length ? withStats.reduce((n, m) => n + pick(m.stats), 0) / withStats.length : 0;
  const sum = pick => withStats.reduce((n, m) => n + pick(m.stats), 0);
  $('fightsCard').hidden = !withStats.length;
  $('fightsNote').textContent = t('fromMatches', { n: number(withStats.length) });
  $('dmgDealt').textContent = number(avg(s => s.damageDealt), 0);
  $('dmgTaken').textContent = number(avg(s => s.damageTaken), 0);
  $('accuracy').textContent = number(avg(s => s.accuracy) * 100, 0) + '%';
  $('bestDamage').textContent = number(Math.max(0, ...withStats.map(m => m.stats.damageDealt)));
  $('assists').textContent = number(sum(s => s.assists));
  $('distance').textContent = number(avg(s => s.traveled) / 100_000, 1);
  $('structures').textContent = number(avg(s => s.damageToStructures), 0);
  $('revives').textContent = number(sum(s => s.revives));
  $('materials').textContent = number(avg(s => s.materialsGathered), 0);
  $('materialsTile').hidden = !withStats.some(m => m.stats.materialsGathered > 0);

  const loot = Object.fromEntries(RARITIES.map(r => [r, 0])), chests = matches.reduce((n, m) => n + (m.chests || 0), 0);
  for (const m of matches) for (const [rarity, n] of Object.entries(m.loot || {})) loot[rarity] = (loot[rarity] || 0) + n;
  const top = Math.max(1, ...Object.values(loot));
  $('loot').replaceChildren(...RARITIES.map(rarity => {
    const row = el('div', null, 'kind'), bar = el('i', null, 'r-' + rarity); bar.style.width = `${loot[rarity] / top * 100}%`;
    const track = el('span'); track.append(bar);
    row.append(el('span', t('rarity_' + rarity)), track, el('b', number(loot[rarity])));
    return row;
  }));
  $('lootNote').textContent = t('weaponsAndItems');
  $('goldPer100').textContent = chests ? number((loot.legendary + loot.mythic) / chests * 100, 1) : '0';
  const best = matches.reduce((b, m) => Math.max(b, (m.loot?.legendary || 0) + (m.loot?.mythic || 0)), 0);
  $('bestLoot').textContent = number(best);
}

// ---- Treasure Hunt --------------------------------------------------------------------------
function renderTreasure() {
  const now = new Date(), summary = summarizeHunt(hunt.matches || [], now, hunt.liveChests || 0);
  renderInsights(hunt.matches || []);

  // Chests by type (all saved replays), on the dashboard
  const kinds = Object.entries(summary.kinds).sort((a, b) => b[1] - a[1]), top = Math.max(1, ...kinds.map(k => k[1]));
  $('kinds').replaceChildren(...(kinds.length ? kinds.map(([name, count]) => {
    const row = el('div', null, 'kind'), bar = el('i'); bar.style.width = `${count / top * 100}%`;
    const track = el('span'); track.append(bar);
    row.append(el('span', name), track, el('b', number(count)));
    return row;
  }) : [el('p', t('noMatches'), 'dim')]));

  // Treasure Hour
  const hour = nextTreasureHour(now);
  $('hourCard').classList.toggle('on', !!hour?.live);
  if (!hour) {
    $('hourLabel').textContent = t('hourOver'); $('hourCountdown').textContent = '—'; $('hourWhen').textContent = '';
  } else {
    const when = new Intl.DateTimeFormat(locale, { weekday: 'long', hour: '2-digit', minute: '2-digit' });
    $('hourLabel').textContent = t(hour.live ? 'hourLive' : 'hourNext');
    $('hourCountdown').textContent = formatLeft((hour.live ? hour.end : hour.start) - now);
    $('hourWhen').textContent = hour.live ? t('hourEnds', { left: formatLeft(hour.end - now) }) : t('hourStarts', { when: `${when.format(hour.start)} – ${new Intl.DateTimeFormat(locale, { hour: '2-digit', minute: '2-digit' }).format(hour.end)}` });
  }

  // This week
  $('weekTitle').textContent = summary.week ? t('weekN', { n: summary.week.n }) : t(now < new Date(WEEK_STARTS[0]) ? 'huntSoon' : 'huntEnded');
  $('weekEnds').textContent = summary.week ? t('weekEnds', { left: formatLeft(summary.week.end - now) }) : '';
  $('weekChests').textContent = number(summary.weekTotal);
  $('huntChests').textContent = number(summary.total);
  $('hourChests').textContent = number(summary.hunt.boosted);

  const rows = luckRows(summary), gizmo = rows.find(r => r.key === 'gizmo');
  if (gizmo.expected >= 1) {
    const ratio = summary.gizmos / gizmo.expected, pct = Math.round(Math.abs(ratio - 1) * 100);
    $('gizmoLuck').textContent = pct < 5 ? t('onAverage') : t(ratio > 1 ? 'luckier' : 'unluckier', { pct });
    $('gizmoLuck').title = t('expected', { n: number(gizmo.expected), found: number(summary.gizmos) });
  } else { $('gizmoLuck').textContent = '—'; $('gizmoLuck').title = ''; }

  // Odds table
  $('oddsRows').replaceChildren(...rows.map(rate => {
    const row = el('tr');
    row.append(el('td', rate.label), el('td', t('oneIn', { n: number(rate.oneIn) }), 'num'), el('td', number(rate.chests), 'num'), el('td', formatChance(rate.chance), 'num count'));
    return row;
  }));

  // Planner
  if (!$('target').options.length) {
    $('target').append(...RATES.map(rate => { const option = el('option', rate.label); option.value = rate.key; return option; }));
    $('target').value = 'pirate';
  }
  const rate = RATES.find(r => r.key === $('target').value) || RATES[1], steps = plan(rate, summary);
  $('planHalf').textContent = number(steps.half);
  $('planNinety').textContent = number(steps.ninety);
  $('planMatches').textContent = steps.matches ? number(steps.matches) : '—';
  $('planEnd').textContent = formatChance(steps.byEnd);
  $('planEndLabel').textContent = t(rate.weekly ? 'planEndWeek' : 'planEndHunt');
  $('planNote').textContent = summary.perDay > 0 ? t('planNote', { perDay: number(summary.perDay, 0), more: number(steps.more) }) : t('planNoPace');

  // Treasure found
  const when = new Intl.DateTimeFormat(locale, { month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit' });
  $('finds').replaceChildren(...(summary.finds.length ? summary.finds.map(find => {
    const item = el('li', null, find.type === 'prize' ? 'prize' : '');
    const name = find.type === 'prize' && /^Unknown treasure/i.test(find.name) ? `${t('goldTreasure')} (${(find.prizeId || '').split(':').pop()})` : find.name;
    item.append(el('b', name), el('span', `${when.format(find.when)} · ${t('from')} ${find.source}`));
    return item;
  }) : [el('li', t('noFinds'))]));
}
async function loadHunt() {
  try { hunt = await api('/api/treasure'); } catch { /* keep the last one */ }
  renderTreasure();
}

// ---- Overlay --------------------------------------------------------------------------------
function renderOverlayPreview() {
  $('previewTotal').textContent = number(snapshot.total || 0);
  $('previewLabel').hidden = !overlay.showLabel; $('previewLabel').textContent = overlay.label.trim() || t('overlayDefaultLabel');
  $('previewLast').hidden = !overlay.showLastMatch; $('previewLast').textContent = '+' + number(snapshot.lastMatch || 0);
  $('overlayUrl').textContent = overlayUrl(); $('previewOverlay').href = overlayUrl();
  $('overlayLabel').placeholder = t('overlayDefaultLabel'); $('overlayLabel').disabled = !overlay.showLabel;
}
function showOverlayOptions(value) {
  overlay = { showLabel: value?.showLabel === true, label: typeof value?.label === 'string' ? value.label.slice(0, 64) : '', showLastMatch: value?.showLastMatch === true };
  $('showOverlayLabel').checked = overlay.showLabel; $('showOverlayLast').checked = overlay.showLastMatch;
  if (document.activeElement !== $('overlayLabel')) $('overlayLabel').value = overlay.label;
}
async function saveOverlay() {
  try {
    await api('/api/set-overlay-options', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(overlay) });
    overlayDirty = false;
  } catch { toast('error'); }
}
for (const id of ['showOverlayLabel', 'overlayLabel', 'showOverlayLast']) {
  $(id).addEventListener(id === 'overlayLabel' ? 'input' : 'change', () => {
    overlay = { showLabel: $('showOverlayLabel').checked, label: $('overlayLabel').value, showLastMatch: $('showOverlayLast').checked };
    overlayDirty = true; renderOverlayPreview();
    clearTimeout(overlayTimer); overlayTimer = setTimeout(saveOverlay, 300);
  });
}

// ---- Settings -------------------------------------------------------------------------------
function showZoom(percent) {
  zoom = Number.isInteger(percent) ? Math.max(50, Math.min(150, percent)) : 100;
  $('zoomValue').textContent = zoom + '%';
  $('zoomOut').disabled = zoom <= 50; $('zoomIn').disabled = zoom >= 150; $('resetZoom').disabled = zoom === 100;
}
async function setZoom(percent) {
  try { showZoom((await api('/api/set-zoom?percent=' + percent)).uiZoomPercent); } catch { toast('error'); }
}
// The counter over the game can be locked or hidden with its shortcuts, so its switches follow what it is doing
async function loadOverlayState() {
  try {
    const state = await api('/api/overlay-state');
    $('gameOverlay').checked = state.visible;
    $('overlayLock').checked = state.locked; $('overlayLock').disabled = !state.visible;
    $('shortcutWarning').hidden = !state.visible || (state.lockShortcut && state.showShortcut);
  } catch { /* older app: leave the switches as they are */ }
}
async function loadSettings() {
  try {
    const data = await api('/api/settings');
    $('folder').value = data.demosFolder || ''; $('startup').checked = !!data.startWithWindows;
    showZoom(data.uiZoomPercent);
    $('gameOverlay').checked = !!data.gameOverlay;
    $('fullscreenWarning').hidden = data.fortniteWindowMode !== 0;
    document.querySelectorAll('.version').forEach(node => { node.textContent = data.version || ''; });
    if (/^v\d+\.\d+\.\d+$/.test(data.version || '')) $('requestFeature').href = `https://burnaviour.dev/request/?for=chest-tracker&v=${data.version.slice(1)}`;
    if (data.overlayOptions && !overlayDirty) showOverlayOptions(data.overlayOptions);
    if (!savedLanguage && data.uiLanguage && data.uiLanguage !== locale) { await setLanguage(data.uiLanguage); $('language').value = locale; }
    settingsLoaded = true;
  } catch { /* tracking still works without the settings */ }
}
async function saveBase(value) {
  const count = parseCount(value); if (count === null) return toast('invalidCount');
  try {
    await api(`/api/set-base?${new URLSearchParams({ count: count ? String(count) : 'clear', profile: snapshot.player || '' })}`);
    await poll(); toast('saved');
  } catch { toast('error'); }
}

// ---- Updates --------------------------------------------------------------------------------
let update = null;
function renderUpdate() {
  if (!update) return;
  const busy = update.state === 'downloading' || update.state === 'restarting';
  const text = update.state === 'downloading' ? t('updateDownloading', { version: update.latest })
    : update.state === 'restarting' ? t('updateRestarting')
    : update.state === 'checking' ? t('updateChecking')
    : update.error ? update.error
    : update.hasUpdate ? t('updateAvailable', { version: update.latest })
    : update.checkedAt ? t('updateLatest') : '';
  $('updateVersion').textContent = update.current;
  if (document.activeElement !== $('updateMode')) $('updateMode').value = update.mode;
  $('updateStatus').textContent = text;
  $('checkUpdate').disabled = busy || update.state === 'checking' || update.mode === 'off';
  $('installUpdate').hidden = !update.hasUpdate || !update.canInstall || busy;
  $('updateNotes').hidden = !update.hasUpdate || !update.notesUrl;
  if (update.notesUrl) $('updateNotes').href = update.notesUrl;
  $('updateSource').hidden = update.canInstall;
  $('updateBanner').hidden = !busy && !(update.hasUpdate && update.canInstall);
  $('updateBannerText').textContent = busy ? text : t('updateAvailable', { version: update.latest });
  $('updateBannerInstall').hidden = busy;
}
async function loadUpdate() {
  try { update = await api('/api/update'); renderUpdate(); } catch { /* the app is restarting or closed */ }
}
async function installUpdate() {
  try { await api('/api/update/install', { method: 'POST' }); await loadUpdate(); } catch { toast('error'); }
}
$('checkUpdate').onclick = async () => {
  $('checkUpdate').disabled = true; $('updateStatus').textContent = t('updateChecking');
  try { update = await api('/api/update/check', { method: 'POST', signal: AbortSignal.timeout(25_000) }); } catch { toast('error'); }
  renderUpdate();
};
$('installUpdate').onclick = installUpdate;
$('updateBannerInstall').onclick = installUpdate;
$('updateMode').onchange = async event => {
  try { update = await api('/api/set-update-mode?mode=' + encodeURIComponent(event.target.value)); renderUpdate(); toast('saved'); } catch { toast('error'); }
};

// ---- Polling --------------------------------------------------------------------------------
async function poll() {
  if (polling) return;
  polling = true;
  try {
    const data = await api('/api/live');
    if (typeof data.total !== 'number') throw new Error('Invalid snapshot');
    snapshot = data; online = true;
    if (data.overlayOptions && !overlayDirty) showOverlayOptions(data.overlayOptions);
    if (!settingsLoaded) await loadSettings();
    await loadOverlayState();
    await loadUpdate();
    // New chests or another profile: the hunt numbers change too
    const key = `${data.player}|${data.total}|${data.matches?.length}|${data.liveChests}`;
    if (key !== huntKey) { huntKey = key; await loadHunt(); }
  } catch { online = false; settingsLoaded = false; }
  finally { polling = false; renderDashboard(); }
}

// ---- Wiring ---------------------------------------------------------------------------------
document.querySelectorAll('.tab').forEach(button => button.addEventListener('click', () => {
  document.querySelectorAll('.tab').forEach(tab => tab.classList.toggle('active', tab === button));
  document.querySelectorAll('.pane').forEach(pane => pane.classList.toggle('active', pane.id === button.dataset.tab));
  document.querySelector('.body').scrollTop = 0;
}));
$('language').append(...languages.map(([code, label]) => { const option = el('option', label); option.value = code; return option; }));
$('language').addEventListener('change', async event => {
  try {
    await setLanguage(event.target.value);
    await api('/api/set-language?code=' + encodeURIComponent(locale));
  } catch { toast('error'); }
});
document.addEventListener('languagechange', () => { renderDashboard(); renderTreasure(); renderUpdate(); });
$('profile').onchange = async event => {
  try { await api('/api/set-profile?key=' + encodeURIComponent(event.target.value)); page = 1; await poll(); } catch { toast('error'); }
};
$('previous').onclick = () => { page--; renderMatches(); };
$('next').onclick = () => { page++; renderMatches(); };
$('export').onclick = () => {
  const url = URL.createObjectURL(new Blob([matchesCsv(snapshot.matches || [])], { type: 'text/csv;charset=utf-8' }));
  const link = el('a'); link.href = url; link.download = 'burnaviour-chests.csv'; link.click(); setTimeout(() => URL.revokeObjectURL(url), 1000);
};
$('target').onchange = renderTreasure;
$('copyOverlay').onclick = () => copy(overlayUrl());
$('creatorCode').onclick = () => copy(CREATOR_CODE);
$('baseForm').onsubmit = event => { event.preventDefault(); saveBase($('base').value); };
$('resetBase').onclick = () => saveBase('');
$('folderForm').onsubmit = async event => {
  event.preventDefault();
  try { await api('/api/set-demos-folder?folder=' + encodeURIComponent($('folder').value)); toast('saved'); } catch { toast('error'); }
};
$('startup').onchange = async event => {
  try { await api('/api/set-startup?enabled=' + event.target.checked); toast('saved'); }
  catch { event.target.checked = !event.target.checked; toast('error'); }
};
$('gameOverlay').onchange = async event => {
  try { await api('/api/set-game-overlay?enabled=' + event.target.checked); await loadOverlayState(); toast('saved'); }
  catch { event.target.checked = !event.target.checked; toast('error'); }
};
$('overlayLock').onchange = async event => {
  try { await api('/api/set-overlay-lock?enabled=' + event.target.checked); toast('saved'); }
  catch { event.target.checked = !event.target.checked; toast('error'); }
};
$('zoomOut').onclick = () => setZoom(zoom - 10);
$('zoomIn').onclick = () => setZoom(zoom + 10);
$('resetZoom').onclick = () => setZoom(100);

// A link can open a tab directly: app.html#tabTreasure
document.querySelector(`.tab[data-tab="${CSS.escape(location.hash.slice(1))}"]`)?.click();

let savedLanguage;
try { savedLanguage = localStorage.getItem(LANGUAGE_KEY); } catch {}
await setLanguage(resolveLanguage(new URLSearchParams(location.search).get('lang') || savedLanguage || navigator.language));
$('language').value = locale;
await poll();
const timer = setInterval(poll, 2500), clock = setInterval(renderTreasure, 30_000);
window.addEventListener('pagehide', () => { clearInterval(timer); clearInterval(clock); });
