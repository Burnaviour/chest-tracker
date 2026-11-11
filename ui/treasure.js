/**
 * Treasure Hunters (1 Nov – 5 Dec 2026): the weeks, Treasure Hours and Epic's published odds per chest,
 * and what they mean for the chests this player really opened. Same numbers as burnaviour.dev/treasure-hunt.
 */
const HOUR = 3_600_000;
const DAY = 24 * HOUR;

/** When each week starts (UTC) */
export const WEEK_STARTS = [
  '2026-11-01T03:00:00Z',
  '2026-11-08T14:00:00Z',
  '2026-11-15T14:00:00Z',
  '2026-11-22T14:00:00Z',
  '2026-11-29T14:00:00Z',
];
/** The End of Chapter event: the hunt ends here */
export const FINALE = '2026-12-05T19:00:00Z';
/** Treasure Hours every weekend (UTC): Saturday 19:00–21:00 and Sunday 02:00–04:00 */
export const TREASURE_HOURS = [
  { weekday: 6, hour: 19, minutes: 120 },
  { weekday: 0, hour: 2, minutes: 120 },
];
const HOURS_FROM = '2026-11-07T00:00:00Z';

/** Epic's published odds per chest (Fortnite Status, 7 Nov 2026), rarest first. `weekly`: a new one every week */
export const RATES = [
  { key: 'outfit', label: 'Weekly Gold Outfit', oneIn: 1_562_500, weekly: true },
  { key: 'pirate', label: 'Pirate Shop Access', oneIn: 137_500, weekly: false },
  { key: 'car', label: 'Weekly Vehicle & Decal', oneIn: 7_813, weekly: true },
  { key: 'gear', label: 'Weekly Pickaxe or Back Bling', oneIn: 568, weekly: true },
  { key: 'track', label: 'Weekly Jam Track', oneIn: 391, weekly: true },
  { key: 'loading', label: 'Weekly Loading Screen', oneIn: 272, weekly: true },
  { key: 'gizmo', label: 'Any Gizmo', oneIn: 127, weekly: false },
];
/** What the Treasure Hours of 7–8 Nov multiplied (later ones may differ) */
export const HOUR_BOOSTS = { outfit: 10, pirate: 2, car: 10 };

/** The week at `now`: { n, start, end } (1-based), or null before the hunt or after the finale */
export function weekAt(now) {
  const t = now.getTime();
  if (t < Date.parse(WEEK_STARTS[0]) || t >= Date.parse(FINALE)) return null;
  let n = 0;
  WEEK_STARTS.forEach((start, i) => { if (t >= Date.parse(start)) n = i + 1; });
  return { n, start: new Date(WEEK_STARTS[n - 1]), end: new Date(WEEK_STARTS[n] ?? FINALE) };
}

/** True when `date` falls inside a Treasure Hour */
export function inTreasureHour(date) {
  const t = date.getTime();
  if (t < Date.parse(HOURS_FROM) || t >= Date.parse(FINALE)) return false;
  const midnight = Date.UTC(date.getUTCFullYear(), date.getUTCMonth(), date.getUTCDate());
  // A session can start the day before, so look one day back too
  for (const d of [0, -1]) {
    const day = new Date(midnight + d * DAY);
    for (const s of TREASURE_HOURS) {
      if (day.getUTCDay() !== s.weekday) continue;
      const start = day.getTime() + s.hour * HOUR;
      if (t >= start && t < start + s.minutes * 60_000) return true;
    }
  }
  return false;
}

/** The Treasure Hour going on at `now`, or the next one; null after the finale */
export function nextTreasureHour(now) {
  const from = Math.max(now.getTime(), Date.parse(HOURS_FROM));
  const base = new Date(from);
  const midnight = Date.UTC(base.getUTCFullYear(), base.getUTCMonth(), base.getUTCDate());
  let best = null;
  for (let d = -1; d <= 7; d++) {
    for (const s of TREASURE_HOURS) {
      const day = new Date(midnight + d * DAY);
      if (day.getUTCDay() !== s.weekday) continue;
      const start = day.getTime() + s.hour * HOUR;
      const end = start + s.minutes * 60_000;
      if (end <= now.getTime() || start < Date.parse(HOURS_FROM) || start >= Date.parse(FINALE)) continue;
      if (!best || start < best.start.getTime()) best = { start: new Date(start), end: new Date(end), live: start <= now.getTime() };
    }
  }
  return best;
}

/**
 * Chance of at least one find at 1-in-`oneIn` odds, over `normal` chests plus `boosted` chests opened
 * during Treasure Hours (odds multiplied by `boost` there)
 */
export function chance(oneIn, normal, boosted = 0, boost = 1) {
  if (!(oneIn >= 1)) return 0;
  const p = 1 / oneIn;
  const miss = Math.max(0, normal) * Math.log1p(-p) + Math.max(0, boosted) * Math.log1p(-Math.min(0.999999, p * boost));
  return 1 - Math.exp(miss);
}

/** Chests needed for a `target` (0–1) chance of at least one find at 1-in-`oneIn` odds */
export function chestsFor(oneIn, target) {
  if (!(oneIn > 1) || !(target > 0) || !(target < 1)) return 0;
  return Math.ceil(Math.log(1 - target) / Math.log1p(-1 / oneIn));
}

/** "0.0064%", "12%", "99.9%+": readable without false precision */
export function formatChance(p) {
  if (!(p > 0)) return '0%';
  if (p >= 0.999) return '99.9%+';
  const pct = p * 100;
  if (pct >= 99.5) return `${Math.floor(pct * 10) / 10}%`;
  if (pct >= 10) return `${Math.round(pct)}%`;
  if (pct >= 1) return `${Number(pct.toFixed(1))}%`;
  if (pct < 0.000001) return 'less than 0.000001%';
  return `${pct.toFixed(Math.min(8, 1 - Math.floor(Math.log10(pct)))).replace(/0+$/, '')}%`;
}

/** "3d 4h", "2h 05m", "12m": time left */
export function formatLeft(ms) {
  const m = Math.max(0, Math.floor(ms / 60_000));
  const d = Math.floor(m / 1440), h = Math.floor((m % 1440) / 60), min = m % 60;
  if (d > 0) return `${d}d ${h}h`;
  if (h > 0) return `${h}h ${String(min).padStart(2, '0')}m`;
  return `${min}m`;
}

/**
 * The player's hunt so far, from the tracker's matches ({ start, chests, times, kinds, finds }):
 * chests since the hunt began and this week (split into normal and Treasure Hour chests), chests by kind,
 * the treasure found, and the pace (chests per match and per day played).
 */
export function summarizeHunt(matches, now, liveChests = 0) {
  const huntStart = Date.parse(WEEK_STARTS[0]), huntEnd = Date.parse(FINALE);
  const week = weekAt(now);
  const hunt = { normal: 0, boosted: 0 }, thisWeek = { normal: 0, boosted: 0 };
  const kinds = {}, finds = [], days = new Set();
  let huntMatches = 0, best = 0;
  for (const m of matches) {
    const start = Date.parse(m.start);
    if (!Number.isFinite(start)) continue;
    for (const [kind, n] of Object.entries(m.kinds || {})) kinds[kind] = (kinds[kind] || 0) + n;
    if (start < huntStart || start >= huntEnd) continue;
    huntMatches++; best = Math.max(best, m.chests || 0);
    days.add(new Date(start).toISOString().slice(0, 10));
    const inWeek = week && start >= week.start.getTime() && start < week.end.getTime();
    const times = Array.isArray(m.times) ? m.times : [];
    // Chests counted but without a recorded time are taken as opened at the match start
    const stamps = [...times.map(s => start + s * 1000), ...Array(Math.max(0, (m.chests || 0) - times.length)).fill(start)].slice(0, m.chests || 0);
    for (const at of stamps) {
      const bucket = inTreasureHour(new Date(at)) ? 'boosted' : 'normal';
      hunt[bucket]++;
      if (inWeek) thisWeek[bucket]++;
    }
    for (const f of m.finds || []) finds.push({ ...f, when: new Date(start + (f.at || 0) * 1000) });
  }
  // The match being played: its chests count now (their exact times come once the replay is finished)
  if (liveChests > 0 && week) {
    const bucket = inTreasureHour(now) ? 'boosted' : 'normal';
    hunt[bucket] += liveChests; thisWeek[bucket] += liveChests;
  }
  const total = hunt.normal + hunt.boosted;
  finds.sort((a, b) => b.when - a.when);
  return {
    week, hunt, thisWeek, kinds, finds, huntMatches, best,
    total, weekTotal: thisWeek.normal + thisWeek.boosted,
    gizmos: finds.filter(f => f.type === 'gizmo').length,
    prizes: finds.filter(f => f.type === 'prize').length,
    perMatch: huntMatches ? total / huntMatches : 0,
    perDay: days.size ? total / days.size : 0,
    daysPlayed: days.size,
    daysLeft: Math.max(0, (huntEnd - now.getTime()) / DAY),
  };
}

/** One row per published rate: the player's chance so far, with the chests that count for it */
export function luckRows(summary) {
  return RATES.map(rate => {
    const pool = rate.weekly ? summary.thisWeek : summary.hunt;
    const boost = HOUR_BOOSTS[rate.key] || 1;
    return {
      ...rate,
      chests: pool.normal + pool.boosted,
      chance: chance(rate.oneIn, pool.normal, pool.boosted, boost),
      expected: (pool.normal + pool.boosted * boost) / rate.oneIn,
    };
  });
}

/** For a target rate: chests for a 50% and a 90% chance, and the chance by the finale at the player's pace */
export function plan(rate, summary) {
  const pool = rate.weekly ? summary.thisWeek : summary.hunt;
  const boost = HOUR_BOOSTS[rate.key] || 1;
  const days = rate.weekly && summary.week ? Math.max(0, (summary.week.end.getTime() - Date.now()) / DAY) : summary.daysLeft;
  const more = Math.round(summary.perDay * days);
  return {
    half: chestsFor(rate.oneIn, 0.5),
    ninety: chestsFor(rate.oneIn, 0.9),
    more,
    byEnd: chance(rate.oneIn, pool.normal + more, pool.boosted, boost),
    matches: summary.perMatch > 0 ? Math.ceil(chestsFor(rate.oneIn, 0.5) / summary.perMatch) : 0,
  };
}
