import { resolveLanguage } from './i18n.js';

// The overlay is served by the tracker itself and reads its saved settings.
const params = new URLSearchParams(location.search);
const cacheKey = 'bct_overlay';
const cleanLabel = value => String(value || '').replace(/[\u0000-\u001f\u007f]/g, '').slice(0,64).trim();
let language = resolveLanguage(params.get('lang') || navigator.language);
let options = { showLabel: params.has('label'), label: cleanLabel(params.get('label')), showLastMatch: params.get('last') === '1' };
let total = null, lastMatch = null, polling = false;
let defaultLabel = 'Chests opened';
const labels = new Map();
const validCount = value => Number.isSafeInteger(value) && value >= 0;
const normalize = value => ({ showLabel: value?.showLabel === true, label: cleanLabel(value?.label), showLastMatch: value?.showLastMatch === true });

function paint() {
  document.documentElement.lang = language;
  document.documentElement.dir = language === 'ar' ? 'rtl' : 'ltr';
  document.body.classList.toggle('detailed', options.showLabel || options.showLastMatch);
  const format = new Intl.NumberFormat(language);
  document.getElementById('total').textContent = total === null ? '—' : format.format(total);
  const caption = document.getElementById('overlayCaption'), last = document.getElementById('overlayLast');
  caption.hidden = !options.showLabel; caption.textContent = options.label || defaultLabel; caption.title = caption.textContent;
  last.hidden = !options.showLastMatch || lastMatch === null;
  last.textContent = lastMatch === null ? '' : '+' + format.format(lastMatch);
}
function remember() {
  try { localStorage.setItem(cacheKey, JSON.stringify({ total, lastMatch, options, language })); } catch {}
}
try {
  const saved = JSON.parse(localStorage.getItem(cacheKey));
  if (saved) {
    if (validCount(saved.total)) total = saved.total;
    if (validCount(saved.lastMatch)) lastMatch = saved.lastMatch;
    if (saved.options) options = normalize(saved.options);
    if (saved.language) language = resolveLanguage(saved.language);
  }
} catch { /* The companion remains usable when browser storage is disabled. */ }
paint();

async function translateLabel() {
  const code = language;
  if (!labels.has(code)) {
    try {
      const response = await fetch(new URL(`./locales/${code}.json`, import.meta.url));
      if (!response.ok) return;
      const dict = await response.json(); labels.set(code, dict.overlayDefaultLabel || 'Chests opened');
    } catch { return; }
  }
  if (language === code) { defaultLabel = labels.get(code); paint(); }
}
async function poll() {
  if (polling) return; polling = true;
  try {
    const response = await fetch('/api/live', { cache: 'no-store', signal: AbortSignal.timeout(1800) });
    if (!response.ok) throw new Error('Companion unavailable');
    const data = await response.json();
    // Saved settings win over anything in the URL.
    if (data.overlayOptions) options = normalize(data.overlayOptions);
    if (data.uiLanguage) language = resolveLanguage(data.uiLanguage);
    // Empty/offline data cannot erase a saved value. A real profile can reset to zero.
    if (validCount(data.total) && (data.total !== 0 || data.player)) {
      total = data.total;
      if (validCount(data.lastMatch)) lastMatch = data.lastMatch;
    }
    paint(); remember();
  } catch { /* Keep the last count and appearance when the companion closes. */ }
  finally { polling = false; }
  if (options.showLabel && !options.label) await translateLabel();
}
await poll();
const timer = setInterval(poll, 2000);
window.addEventListener('pagehide', () => clearInterval(timer));
