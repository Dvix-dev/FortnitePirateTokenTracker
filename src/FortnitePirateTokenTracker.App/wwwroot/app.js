const state = {
  mode: 'server',
  config: null,
  report: null,
  matches: [],
  files: [],
  catalog: [],
  folderExists: false,
  replayCount: 0,
  paths: null
};

const $ = (id) => document.getElementById(id);

/** Un contenedor es tesoro si su clase replicada lo marca como tal. */
function isTreasure(chest) {
  return /tesoro|dorado/i.test(chest?.containerClass || '');
}

function esc(value) {
  return String(value ?? '')
    .replaceAll('&', '&amp;')
    .replaceAll('<', '&lt;')
    .replaceAll('>', '&gt;')
    .replaceAll('"', '&quot;');
}

/** 1789 → «1.789»: el español omite el separador en los números de cuatro cifras. */
function num(value) {
  return Number(value).toLocaleString('de-DE');
}

function toast(message, isError = false) {
  const el = $('toast');
  el.textContent = message;
  el.classList.toggle('is-error', isError);
  el.hidden = false;
  clearTimeout(toast._timer);
  toast._timer = setTimeout(() => { el.hidden = true; }, 3200);
}

async function api(path, options) {
  const response = await fetch(path, {
    headers: { 'Content-Type': 'application/json' },
    ...options
  });
  const data = await response.json().catch(() => ({}));
  if (!response.ok || data.error) {
    throw new Error(data.error || `Error ${response.status}`);
  }
  return data;
}

/* ---------------- resumen ---------------- */

function renderStats() {
  const report = state.report;
  const totals = report?.totals;

  const treasures = (report?.chests ?? []).filter((c) => /tesoro|dorado/i.test(c.containerClass || ''));
  const matchesWithTreasure = new Set(treasures.map((c) => c.replayFile)).size;
  const myTreasures = treasures.filter((c) => c.isLocalPlayer).length;
  const named = treasures.filter((c) => {
    const raw = c.itemId || '';
    return raw && !/sin identificar|cosmetics\.variant|immutable|tesoro del evento/i.test(raw);
  }).length;
  const analyzed = totals?.replaysAnalyzed ?? 0;
  const treasureRate = analyzed === 0 ? 0 : Math.round((matchesWithTreasure / analyzed) * 1000) / 10;

  const cards = [
    { label: 'Tesoros detectados', value: treasures.length, sub: `${matchesWithTreasure} partidas`, gold: true },
    { label: 'Tesoros míos', value: myTreasures, sub: 'abiertos por ti', gold: true },
    { label: 'Premios identificados', value: named, sub: `de ${treasures.length} tesoros`, gold: false },
    { label: 'Partidas con tesoro', value: `${treasureRate}%`, sub: `${matchesWithTreasure} de ${analyzed}`, gold: false }
  ];

  $('statsGrid').innerHTML = cards.map((card) => `
    <div class="stat ${card.gold ? 'is-gold' : ''}">
      <div class="stat-label">${esc(card.label)}</div>
      <div class="stat-value">${esc(typeof card.value === 'number' ? num(card.value) : card.value)}</div>
      <div class="stat-sub">${esc(card.sub)}</div>
    </div>
  `).join('');

  $('tokenRatePill').textContent = totals ? `${treasureRate}% de partidas` : '—';
  $('rateNote').textContent = totals
    ? `${matchesWithTreasure} partidas con tesoro · ${named} premios identificados · ${treasures.length - named} sin identificar.`
    : 'Analiza tus repeticiones para ver cuántos tesoros salen.';

  const max = Math.max(1, totals?.chestsDetected ?? 1);
  const rows = [
    { label: 'Cofres totales', value: totals?.chestsDetected ?? 0 },
    { label: 'Tesoros', value: treasures.length },
    { label: 'Tesoros míos', value: myTreasures },
    { label: 'Cofres míos', value: totals?.chestsByMe ?? 0 }
  ];

  $('rateBars').innerHTML = rows.map((row) => `
    <div class="bar-row">
      <div class="bar-head"><span>${esc(row.label)}</span><span>${esc(num(row.value))}</span></div>
      <div class="bar-track"><div class="bar-fill" style="width:${Math.min(100, (row.value / max) * 100)}%"></div></div>
    </div>
  `).join('');

  const warnings = report?.warnings ?? [];
  $('warningCard').hidden = warnings.length === 0;
  $('warningCount').textContent = warnings.length;
  $('warningList').innerHTML = warnings.slice(0, 12).map((w) => `<li>${esc(w)}</li>`).join('');
}

/* ---------------- cofres ---------------- */

/** «UnsavedReplay-2026.11.15-18.53.59.replay» → «15/11/2026 - 18:53». */
function matchLabel(file) {
  const match = /(\d{4})\.(\d{2})\.(\d{2})-(\d{2})\.(\d{2})/.exec(file || '');
  return match ? `${match[3]}/${match[2]}/${match[1]} - ${match[4]}:${match[5]}` : (file || '—');
}

/** Instante de la partida según su nombre de fichero (el mismo que se muestra). */
function matchTime(file) {
  const match = /(\d{4})\.(\d{2})\.(\d{2})-(\d{2})\.(\d{2})/.exec(file || '');
  return match ? Date.parse(`${match[1]}-${match[2]}-${match[3]}T${match[4]}:${match[5]}:00Z`) : 0;
}

/** Ordena de la partida más reciente a la más antigua y, dentro, del evento más tardío. */
function byMatchThenTime(a, b) {
  const porPartida = matchTime(b.replayFile) - matchTime(a.replayFile);
  return porPartida !== 0 ? porPartida : (b.timeMs ?? 0) - (a.timeMs ?? 0);
}

function renderChests() {
  const onlyTokens = $('onlyTokens').checked;

  // La más reciente arriba: el informe llega en el orden en que se guardó cada partida, que
  // no es el de sus fechas, y así la lista parecía no cambiar nunca.
  const chests = (state.report?.chests ?? [])
    .filter((c) => !onlyTokens || isTreasure(c))
    .sort(byMatchThenTime);
  const body = $('chestTable').querySelector('tbody');

  body.innerHTML = chests.slice(0, 400).map((c) => `
    <tr>
      <td class="mono">${esc(matchLabel(c.replayFile))}</td>
      <td class="mono">${esc(c.timeLabel)}</td>
      <td>${esc(c.containerClass)}${c.itemId ? `<div class="muted small mono">${esc(c.itemId)}</div>` : ''}</td>
      <td>${c.isLocalPlayer ? '<span class="tag tag-local">yo</span>' : esc(c.playerName ?? '—')}</td>
      <td><span class="conf conf-${esc((c.confidence || 'c').toLowerCase())}">${esc(c.confidence)}</span></td>
      <td class="num">${esc(c.x)}</td>
      <td class="num">${esc(c.y)}</td>
      <td>${esc(c.zone)}${c.positionApproximate ? ' <span class="tag">aprox.</span>' : ''}</td>
    </tr>
  `).join('');

  const unattributed = (state.report?.chests ?? []).filter((c) => !c.playerName).length;
  $('chestAttributionNote').textContent = unattributed > 0
    ? `${unattributed} aperturas sin jugador atribuido: son cofres cuyo grupo replica la búsqueda con una clase del lector oficial que no expone el pawn ni la posición, así que no hay con qué casarlos.`
    : '';

  $('chestUpdated').textContent = state.loadedAt
    ? `${chests.length.toLocaleString('es-ES')} eventos · actualizado ${state.loadedAt}`
    : '';

  $('chestEmpty').hidden = chests.length > 0;
  $('chestEmpty').textContent = state.report
    ? 'No hay cofres que coincidan con el filtro.'
    : 'Sin cofres registrados todavía. Analiza tus repeticiones desde el botón «Analizar ahora».';
}

/* ---------------- premios ---------------- */

function renderPrizes() {
  const treasures = (state.report?.chests ?? [])
    .filter((c) => /tesoro|dorado/i.test(c.containerClass || ''))
    .sort(byMatchThenTime);

  $('treasureCount').textContent = treasures.length;

  const unknown = treasures.filter((c) => {
    const raw = c.itemId || '';
    return !raw || /cosmetics\.variant|immutable|sin identificar|tesoro del evento/i.test(raw);
  }).length;
  $('treasureUnknown').textContent = unknown > 0
    ? `${unknown} llegaron como «tesoro del evento»: la repetición no envía qué premio cayó, solo quién lo recibió y cuándo.`
    : '';
  const mine = treasures.filter((c) => c.isLocalPlayer).length;
  $('treasureSummary').textContent = treasures.length === 0
    ? ''
    : `${mine} ${mine === 1 ? 'tuyo' : 'tuyos'} · ${new Set(treasures.map((c) => c.replayFile)).size} partidas`;
  $('treasureEmpty').hidden = treasures.length > 0;
  $('treasureList').innerHTML = treasures.slice(0, 30).map((c) => {
    const seconds = Math.floor((c.timeMs ?? 0) / 1000);
    const when = `${Math.floor(seconds / 60)}:${String(seconds % 60).padStart(2, '0')}`;
    const who = c.isLocalPlayer ? 'tú' : (c.playerName || 'sin identificar');
    // Los análisis antiguos pudieron guardar basura del parser anterior como identificador.
    const raw = c.itemId || '';
    const label = raw && !/cosmetics\.variant|immutable|sin identificar|tesoro del evento/i.test(raw) ? raw : 'premio sin identificar';
    const source = /dorado/i.test(c.containerClass || '') ? 'cofre dorado' : 'habilidad del evento';
    return `<li><strong>${esc(matchLabel(c.replayFile))}</strong> · ${esc(when)} de partida · ${esc(label)} · ${esc(who)} · ${esc(source)}</li>`;
  }).join('');

  const found = new Map((state.report?.categories ?? []).map((c) => [c.key, c.found]));
  $('counterSkin').textContent = found.get('outfit') ?? 0;
  $('counterToken').textContent = found.get('token') ?? 0;
  $('counterCar').textContent = found.get('vehicle') ?? 0;
  $('counterOther').textContent = unknown;
  $('countersNote').textContent = `${treasures.length} tesoros en total · «otros» son los que no se han podido identificar`;

  // De más a menos probable: «1 en 127» antes que «1 en 1.562.500».
  const odds = (text) => {
    const match = /1\s*en\s*([\d.,]+)/i.exec(text || '');
    return match ? 1 / Number(match[1].replace(/[.,]/g, '')) : 0;
  };
  const catalog = [...state.catalog].sort((a, b) => odds(b.odds) - odds(a.odds));

  $('prizeList').innerHTML = catalog.map((prize) => `
    <div class="prize">
      <div class="prize-thumb" style="background:linear-gradient(140deg, ${esc(prize.accent)}33, ${esc(prize.accent)}0d)">
        <img src="img/${esc(prize.image)}" alt="${esc(prize.label)}" loading="lazy">
      </div>
      <div class="prize-body">
        <div class="prize-name">${esc(prize.label)}${(found.get(prize.key) ?? 0) > 0 ? '<span class="prize-hit">visto</span>' : ''}</div>
        <div class="prize-desc">${esc(prize.description)}</div>
      </div>
      <div class="prize-right">
        <div class="prize-odds">${esc(prize.odds)}</div>
      </div>
    </div>
  `).join('');
}

/* ---------------- partidas ---------------- */

function renderMatches() {
  const matches = [...(state.report?.matches ?? [])].reverse();

  const treasuresByFile = new Map();
  for (const chest of state.report?.chests ?? []) {
    if (!isTreasure(chest)) continue;
    treasuresByFile.set(chest.replayFile, (treasuresByFile.get(chest.replayFile) ?? 0) + 1);
  }

  $('matchesList').innerHTML = matches.map((match) => `
    <div class="match">
      <div class="match-main">
        <div class="match-name">${esc(matchLabel(match.replayFile))}</div>
        <div class="match-meta">
          ${esc(match.playedAt ? new Date(match.playedAt).toLocaleString('es-ES') : 'fecha desconocida')}
          · ${esc(match.mode)} · ${esc(Math.round((match.lengthSeconds ?? 0) / 60))} min · ${esc(match.players)} jugadores
        </div>
        ${match.error ? `<div class="match-error">${esc(match.error)}</div>` : ''}
      </div>
      <div class="match-numbers">
        <span>Cofres<b>${esc(match.chests)}</b></span>
        <span>Míos<b>${esc(match.myChests)}</b></span>
        <span>Tesoros<b>${esc(treasuresByFile.get(match.replayFile) ?? 0)}</b></span>
      </div>
    </div>
  `).join('');

  $('matchesEmpty').hidden = matches.length > 0;
}

/* ---------------- mapa de calor ---------------- */

// El mapa del juego está en unidades de Unreal (1 unidad = 1 cm). La transformación está
// ajustada por mínimos cuadrados con las coordenadas reales de los POI que publica la API
// del juego y rematada para que ningún cofre caiga en el agua. Ojo: +Y va hacia el sur, así
// que la fila crece con la coordenada, sin invertir.
const MAP_IMG = { w: 560, h: 555 };
const MAP_SCALE = 0.00295;
const MAP_OFFSET = { x: 246, y: 208 };
const MAP_CELL = 5000;
const mapPrefs = { opacity: 0.5, shape: 'blobs', metric: 'chests' };

function mapPoint(x, y) {
  return { x: MAP_SCALE * x + MAP_OFFSET.x, y: MAP_SCALE * y + MAP_OFFSET.y };
}

// POI del mapa actual, en coordenadas del mundo (API oficial). Sirven para nombrar zonas.
const POIS = [
  ['Arlong Park', -51707, 16134],
  ['Baratie', 65771, 69662],
  ['Buggy’s Beachhead', 8270, -25088],
  ['Busy Bay', 62269, 70367],
  ['Cursed Cavern', 31432, 19822],
  ['Giant’s Battlefield', -3614, 34262],
  ['Henry', 34307, 73225],
  ['Lantern Landing', -11770, 4994],
  ['Loguetown', 1846, 55330],
  ['Nami’s Tangerine Grove', -40304, 82390],
  ['Plunder Port', 26307, 89235],
  ['Rubber Resort', -24028, -45216],
  ['The $.$. Rocky', 66471, 30308],
  ['The Chest', -32433, -21391],
  ['Windmill Village', -33592, 63719]
];

// Centro aproximado de la isla, para orientar lo que cae lejos de cualquier POI.
const MAP_CENTER = { x: 12500, y: 25000 };

function nearestPoi(x, y) {
  let best = null;
  for (const [name, px, py] of POIS) {
    const distance = Math.hypot(x - px, y - py) / 100;
    if (!best || distance < best.distance) best = { name, distance };
  }
  return best;
}

/** Nombre legible de una posición: el POI más cercano o la dirección desde el centro. */
function zoneName(x, y) {
  const near = nearestPoi(x, y);
  if (near && near.distance <= 250) {
    return { name: near.name, meters: Math.round(near.distance), kind: 'poi' };
  }

  const dx = x - MAP_CENTER.x;
  const dy = y - MAP_CENTER.y;
  const compass = ['este', 'sureste', 'sur', 'suroeste', 'oeste', 'noroeste', 'norte', 'noreste'];
  const angle = (Math.atan2(dy, dx) * 180 / Math.PI + 360) % 360;
  return {
    name: `Campo abierto al ${compass[Math.round(angle / 45) % 8]}`,
    meters: Math.round(Math.hypot(dx, dy) / 100),
    kind: 'field'
  };
}

/** Amarillo (poco) → naranja → rojo (mucho). */
function heatColor(t, alpha) {
  const stops = [[255, 224, 102], [255, 176, 32], [255, 59, 48]];
  const v = Math.max(0, Math.min(1, t)) * (stops.length - 1);
  const i = Math.min(stops.length - 2, Math.floor(v));
  const f = v - i;
  const rgb = stops[i].map((c, k) => Math.round(c + (stops[i + 1][k] - c) * f));
  return `rgba(${rgb[0]}, ${rgb[1]}, ${rgb[2]}, ${alpha})`;
}

function renderMap() {
  const canvas = $('mapHeat');
  const img = $('mapImg');
  if (!canvas || !img) return;
  if (!img.complete || !img.naturalWidth) {
    img.addEventListener('load', () => renderMap(), { once: true });
    return;
  }

  const scale = (img.clientWidth || MAP_IMG.w) / MAP_IMG.w;
  const dpr = window.devicePixelRatio || 1;
  canvas.width = Math.round(MAP_IMG.w * scale * dpr);
  canvas.height = Math.round(MAP_IMG.h * scale * dpr);

  const ctx = canvas.getContext('2d');
  ctx.setTransform(scale * dpr, 0, 0, scale * dpr, 0, 0);
  ctx.clearRect(0, 0, MAP_IMG.w, MAP_IMG.h);

  const metric = mapPrefs.metric;
  const chests = state.report?.chests ?? [];
  const source = metric === 'treasures' ? chests.filter(isTreasure) : chests;
  const cells = new Map();
  let placed = 0;
  let total = 0;

  for (const chest of source) {
    total++;
    const x = Number(chest.x);
    const y = Number(chest.y);
    if (!x || !y) continue;
    placed++;
    const key = `${Math.floor(x / MAP_CELL)}:${Math.floor(y / MAP_CELL)}`;
    cells.set(key, (cells.get(key) ?? 0) + 1);
  }

  // Escala logarítmica: casi todas las celdas tienen pocos cofres y unas pocas muchos, así que
  // sin comprimir la escala el mapa saldría amarillo entero.
  const counts = [...cells.values()].sort((a, b) => a - b);
  const peak = counts.length ? counts[counts.length - 1] : 0;
  const max = Math.max(2, counts.length ? counts[Math.floor(counts.length * 0.95)] : 2);
  const weight = (n) => Math.log1p(n) / Math.log1p(max);

  const cellPx = Math.abs(mapPoint(MAP_CELL, 0).x - mapPoint(0, 0).x);
  const blobs = mapPrefs.shape === 'blobs';
  // De menor a mayor carga: así las zonas calientes quedan por encima y no las tapan las frías.
  const ordered = blobs ? [...cells].sort((a, b) => a[1] - b[1]) : [...cells];

  for (const [key, n] of ordered) {
    const [cx, cy] = key.split(':').map(Number);
    const a = mapPoint(cx * MAP_CELL, cy * MAP_CELL);
    const b = mapPoint((cx + 1) * MAP_CELL, (cy + 1) * MAP_CELL);
    const left = Math.min(a.x, b.x);
    const top = Math.min(a.y, b.y);
    const w = Math.abs(b.x - a.x) - 2;
    const h = Math.abs(b.y - a.y) - 2;
    if (w <= 0 || h <= 0) continue;

    // Las celdas con uno o dos cofres (posiciones aproximadas en la costa) se aclaran para que
    // no tapen el mapa como si fueran zonas cargadas.
    const t = weight(n);
    const alpha = mapPrefs.opacity * (0.34 + 0.66 * t);

    if (blobs) {
      const cxp = left + (w + 2) / 2;
      const cyp = top + (h + 2) / 2;
      const radius = cellPx * 1.15;
      const gradient = ctx.createRadialGradient(cxp, cyp, 0, cxp, cyp, radius);
      gradient.addColorStop(0, heatColor(t, alpha));
      gradient.addColorStop(0.5, heatColor(t, alpha * 0.55));
      gradient.addColorStop(1, heatColor(t, 0));
      ctx.fillStyle = gradient;
      ctx.beginPath();
      ctx.arc(cxp, cyp, radius, 0, Math.PI * 2);
      ctx.fill();
      continue;
    }

    ctx.fillStyle = heatColor(t, alpha);
    ctx.beginPath();
    ctx.roundRect(left + 1, top + 1, w, h, 3);
    ctx.fill();
  }

  // Los tesoros con posición se marcan encima, para que se vean aunque el calor sea de cofres.
  for (const chest of chests) {
    if (!/tesoro|dorado/i.test(chest.containerClass || '')) continue;
    const x = Number(chest.x);
    const y = Number(chest.y);
    if (!x || !y) continue;
    const p = mapPoint(x, y);
    ctx.beginPath();
    ctx.arc(p.x, p.y, 4.4, 0, Math.PI * 2);
    ctx.fillStyle = '#fff7cc';
    ctx.fill();
    ctx.lineWidth = 1.6;
    ctx.strokeStyle = '#0a0a0b';
    ctx.stroke();
  }

  // Una celda son 50 m × 50 m = 0,0025 km², así que la densidad se lee directa en cofres/km².
  const area = cells.size * 0.0025;
  const density = area > 0 ? Math.round(placed / area) : 0;
  $('mapTitle').textContent = metric === 'treasures' ? 'Densidad de tesoros' : 'Densidad de cofres';

  if (metric === 'treasures') {
    const missing = total - placed;
    $('mapNote').textContent = `${placed} de ${total} tesoros traen posición (${Math.round(placed / Math.max(1, cells.size) * 10) / 10} por celda, máx. ${peak}); los otros ${missing} llegan por la habilidad sin coordenadas, así que no se pueden situar.`;
    return;
  }

  $('mapNote').textContent = `${placed.toLocaleString('es-ES')} aperturas en ${cells.size.toLocaleString('es-ES')} celdas de 50 m (${density.toLocaleString('es-ES')} cofres/km² de media, máx. ${peak} por celda). Más rojo = más densidad; los puntos blancos son los tesoros.`;
}

window.addEventListener('resize', () => renderMap());

/* ---------------- zonas ---------------- */

function renderZones() {
  const chests = state.report?.chests ?? [];
  const groups = new Map();
  let sinPosicion = 0;

  for (const chest of chests) {
    const x = Number(chest.x);
    const y = Number(chest.y);
    if (!x || !y) {
      sinPosicion++;
      continue;
    }

    const zone = zoneName(x, y);
    if (!groups.has(zone.name)) {
      groups.set(zone.name, { name: zone.name, kind: zone.kind, chests: 0, treasures: 0, distance: 0 });
    }

    const group = groups.get(zone.name);
    group.chests++;
    group.distance += zone.meters;
    if (isTreasure(chest)) group.treasures++;
  }

  const zones = [...groups.values()]
    .map((zone) => ({ ...zone, distance: Math.round(zone.distance / zone.chests) }))
    .sort((a, b) => b.chests - a.chests || b.treasures - a.treasures);

  $('zonesList').innerHTML = zones.map((zone) => `
    <div class="zone">
      <span class="zone-name">${esc(zone.name)}</span>
      <span class="tag">${esc(zone.chests)} cofres</span>
      <span class="tag ${zone.treasures > 0 ? 'tag-local' : ''}">${esc(zone.treasures)} tesoros</span>
      <span class="zone-meta">${esc(zone.distance)} m ${zone.kind === 'poi' ? 'del POI' : 'del centro'}</span>
    </div>
  `).join('');

  $('zonesEmpty').hidden = zones.length > 0;
  if (zones.length === 0) {
    $('zonesEmpty').textContent = 'Sin coordenadas registradas todavía. Los cofres sin posición replicada no se agrupan.';
  }

  const note = $('zonesNote');
  if (note) {
    note.textContent = sinPosicion > 0
      ? `${sinPosicion.toLocaleString('es-ES')} aperturas no replican posición y no entran en las zonas. Cada zona es el POI a menos de 250 m; el resto se agrupa por dirección desde el centro del mapa.`
      : 'Cada zona es el POI a menos de 250 m; el resto se agrupa por dirección desde el centro del mapa.';
  }
}

/* ---------------- configuración ---------------- */

function renderConfig() {
  const config = state.config;
  if (!config) return;

  $('cfgReplayFolder').value = config.replayFolder ?? '';
  $('cfgRadius').value = config.radiusMeters ?? 1000;
  $('cfgPort').value = config.port ?? 5178;
  $('cfgConfidence').value = config.minConfidence ?? 'B';
  $('cfgSeparator').value = config.csvSeparator ?? ';';
  $('cfgAnonymize').checked = Boolean(config.anonymize);

  $('folderHint').textContent = state.folderExists
    ? `Carpeta válida · ${state.replayCount} repeticiones encontradas.`
    : 'La carpeta no existe o no contiene archivos .replay.';

  const paths = state.paths ?? {};
  $('pathsList').innerHTML = `
    <li>Repeticiones: <span class="mono">${esc(paths.replays)}</span></li>
    <li>Informe: <span class="mono">${esc(paths.report)}</span></li>
    <li>CSV: <span class="mono">${esc(paths.csv)}</span></li>
  `;
}

/* ---------------- mis cofres ---------------- */

function renderMyChests() {
  const chests = state.report?.chests ?? [];
  const mineOpened = $('countMineOpened').checked;
  const mineDestroyed = $('countMineDestroyed').checked;
  const squad = $('countSquad').checked;
  const others = $('countOthers').checked;

  let total = 0;
  let opened = 0;
  let broken = 0;
  let mates = 0;
  let otros = 0;

  for (const chest of chests) {
    if (chest.isLocalPlayer) {
      if (chest.destroyed) {
        broken++;
        if (mineDestroyed) total++;
      } else {
        opened++;
        if (mineOpened) total++;
      }
    } else if (chest.isSquad) {
      mates++;
      if (squad) total++;
    } else if (chest.playerName) {
      otros++;
      if (others) total++;
    }
  }

  $('myChestsSum').textContent = total.toLocaleString('es-ES');
  $('myChestsNote').textContent =
    `${num(opened)} míos abiertos · ${num(broken)} rotos por mí · ${num(mates)} de mi squad · ${num(otros)} de otros jugadores.`;
}

['countMineOpened', 'countMineDestroyed', 'countSquad', 'countOthers'].forEach((id) => {
  $(id).addEventListener('change', renderMyChests);
});

/* ---------------- estado global ---------------- */

function renderStatus() {
  const has = Boolean(state.report);
  $('statusDot').classList.toggle('is-live', has || state.folderExists);
  $('statusText').textContent = has
    ? `${state.report.totals.replaysAnalyzed} partidas analizadas`
    : state.folderExists
      ? `${state.replayCount} repeticiones listas`
      : 'Carpeta de repeticiones no encontrada';
  $('footerVersion').textContent = state.version ? `v${state.version}` : '';
}

function renderAll() {
  renderStatus();
  renderStats();
  renderMyChests();
  renderChests();
  renderPrizes();
  renderMatches();
  renderZones();
  renderMap();
  renderConfig();
}

/* ---------------- eventos ---------------- */

document.querySelectorAll('.tab').forEach((tab) => {
  tab.addEventListener('click', () => {
    document.querySelectorAll('.tab').forEach((t) => t.classList.toggle('is-active', t === tab));
    document.querySelectorAll('.view').forEach((view) => {
      view.classList.toggle('is-active', view.id === `view-${tab.dataset.view}`);
    });
  });
});

$('onlyTokens').addEventListener('change', renderChests);
$('mapMetric').addEventListener('click', (event) => {
  const button = event.target.closest('button[data-metric]');
  if (!button) return;
  mapPrefs.metric = button.dataset.metric;
  for (const option of $('mapMetric').children) {
    option.classList.toggle('is-on', option === button);
  }
  renderMap();
});

$('mapSettingsBtn').addEventListener('click', (event) => {
  event.stopPropagation();
  const pop = $('mapSettings');
  pop.hidden = !pop.hidden;
  $('mapSettingsBtn').setAttribute('aria-expanded', String(!pop.hidden));
});

$('mapSettings').addEventListener('click', (event) => event.stopPropagation());

document.addEventListener('click', () => {
  $('mapSettings').hidden = true;
  $('mapSettingsBtn').setAttribute('aria-expanded', 'false');
});

document.addEventListener('keydown', (event) => {
  if (event.key === 'Escape') {
    $('mapSettings').hidden = true;
    $('mapSettingsBtn').setAttribute('aria-expanded', 'false');
  }
});

$('mapOpacity').addEventListener('input', (event) => {
  const value = Number(event.target.value);
  mapPrefs.opacity = value / 100;
  $('mapOpacityValue').textContent = `${value}%`;
  renderMap();
});

$('mapShape').addEventListener('click', (event) => {
  const button = event.target.closest('button[data-shape]');
  if (!button) return;
  mapPrefs.shape = button.dataset.shape;
  for (const option of $('mapShape').children) {
    option.classList.toggle('is-on', option === button);
  }
  renderMap();
});

function openScanModal() {
  const total = state.replayCount ?? 0;
  $('choiceAllCount').textContent = total ? `${total} archivos` : 'sin archivos';
  $('scanModalInfo').textContent = total
    ? `Hay ${total} repeticiones en la carpeta. Elige cuántas procesar.`
    : 'No hay repeticiones en la carpeta configurada.';
  $('scanCustom').value = '';
  document.querySelector('input[name="scanCount"][value="all"]').checked = true;
  $('scanModal').hidden = false;
  $('scanCustom').focus();
}

function closeScanModal() {
  $('scanModal').hidden = true;
}

function readScanLimit() {
  const custom = $('scanCustom').value.trim();
  if (custom !== '') {
    const parsed = Number(custom);
    if (!Number.isFinite(parsed) || parsed < 1) {
      throw new Error('Escribe una cantidad mayor que cero');
    }
    return Math.min(Math.floor(parsed), 5000);
  }

  const selected = document.querySelector('input[name="scanCount"]:checked');
  if (!selected || selected.value === 'all') {
    return null;
  }

  return Number(selected.value);
}

async function runScan(limit) {
  const button = $('scanConfirm');
  const topButton = $('scanBtn');
  button.disabled = true;
  topButton.disabled = true;
  button.textContent = 'Analizando…';

  try {
    await api('/api/scan', { method: 'POST', body: JSON.stringify({ limit }) });
    await load();
    closeScanModal();
    toast(limit ? `Analizadas las últimas ${limit} partidas` : 'Análisis completado');
  } catch (error) {
    toast(error.message, true);
  } finally {
    button.disabled = false;
    topButton.disabled = false;
    button.textContent = 'Analizar';
  }
}

$('scanBtn').addEventListener('click', () => {
  if (state.mode === 'local') {
    $('replayInput').click();
    return;
  }
  openScanModal();
});

$('scanConfirm').addEventListener('click', () => {
  try {
    const limit = readScanLimit();
    runScan(limit);
  } catch (error) {
    toast(error.message, true);
  }
});

$('scanModal').addEventListener('click', (event) => {
  if (event.target.hasAttribute('data-close')) {
    closeScanModal();
  }
});

document.addEventListener('keydown', (event) => {
  if (event.key === 'Escape' && !$('scanModal').hidden) {
    closeScanModal();
  }
});

$('scanCustom').addEventListener('input', () => {
  document.querySelectorAll('input[name="scanCount"]').forEach((radio) => { radio.checked = false; });
});

document.querySelectorAll('input[name="scanCount"]').forEach((radio) => {
  radio.addEventListener('change', () => { $('scanCustom').value = ''; });
});

$('configForm').addEventListener('submit', async (event) => {
  event.preventDefault();
  const payload = {
    replayFolder: $('cfgReplayFolder').value.trim(),
    radiusMeters: Number($('cfgRadius').value) || 1000,
    port: Number($('cfgPort').value) || 5178,
    minConfidence: $('cfgConfidence').value,
    csvSeparator: $('cfgSeparator').value,
    anonymize: $('cfgAnonymize').checked
  };

  try {
    const result = await api('/api/config', { method: 'POST', body: JSON.stringify(payload) });
    state.config = result.config;
    renderConfig();
    $('saveState').textContent = 'Guardado';
    toast('Configuración guardada');
    setTimeout(() => { $('saveState').textContent = ''; }, 2500);
  } catch (error) {
    toast(error.message, true);
  }
});

async function load() {
  try {
    const data = await TTStore.load();
    Object.assign(state, data);
    state.loadedAt = new Date().toLocaleTimeString('es-ES');
    applyMode();
    renderAll();

    if (state.mode === 'local' && (state.catalog?.length ?? 0) === 0) {
      ensureCatalog();
    }
  } catch (error) {
    toast(`No se pudo cargar el panel: ${error.message}`, true);
  }
}

/** El catálogo de premios vive en el motor; se pide una vez y se guarda en el navegador. */
async function ensureCatalog() {
  try {
    const catalog = await TTEngine.catalog();
    state.catalog = catalog;
    await TTStore.saveCatalog(catalog);
    renderPrizes();
  } catch {
    // Sin motor el catálogo se queda vacío; el resto del panel funciona igual.
  }
}

/** Ajusta el panel al modo: exe local (carpeta) o web estática (arrastrar .replay). */
function applyMode() {
  const local = state.mode === 'local';

  $('uploadCard').hidden = !local;

  $('browserDataCard').hidden = !local;
  $('browserDataCount').textContent = local ? `${state.files?.length ?? 0} archivos analizados` : '';
  $('configForm').hidden = local;
  $('outputPathsCard').hidden = local;
  $('serverGuide').hidden = local;
  $('guideIntro').textContent = local
    ? 'De cero a datos en cuatro pasos, sin instalar nada.'
    : 'Con el programa local: de cero a datos en cuatro pasos.';
  $('scanBtn').textContent = local ? 'Añadir repeticiones' : 'Analizar ahora';
  $('uploadCount').textContent = local
    ? `${state.files?.length ?? 0} analizados hasta ahora`
    : '';

  const configTab = document.querySelector('button[data-view="configuracion"]');
  if (configTab) {
    configTab.textContent = local ? 'Mis datos' : 'Configuración';
  }
}

/* ---------------- subida de repeticiones (web estática) ---------------- */

function showProcessing(title, step) {
  $('processingTitle').textContent = title;
  $('processingStep').textContent = step ?? '';
  $('processingBar').style.width = '0%';
  $('processing').hidden = false;
}

const paint = () => new Promise((resolve) => setTimeout(resolve, 40));

async function handleReplayFiles(list) {
  const files = [...(list ?? [])].filter((file) => file.name.toLowerCase().endsWith('.replay'));
  if (files.length === 0) {
    toast('Elige archivos .replay', true);
    return;
  }

  showProcessing('Analizando repeticiones…', 'Cargando el motor (solo la primera vez)…');
  await paint();

  try {
    await TTEngine.runtime();
    const total = files.length;

    for (let i = 0; i < total; i++) {
      $('processingStep').textContent = `${i + 1} de ${total} · ${files[i].name}`;
      $('processingBar').style.width = `${Math.round((i / total) * 92)}%`;
      await paint();
      const match = await TTEngine.analyze(files[i]);
      await TTStore.addMatch(files[i].name, match);
    }

    $('processingStep').textContent = 'Calculando el informe…';
    $('processingBar').style.width = '96%';
    await paint();

    const guardado = await TTStore.load();
    const report = await TTEngine.buildReport(guardado.matches, guardado.config ?? {});
    await TTStore.saveReport(report);

    await load();
    $('processingBar').style.width = '100%';
    $('processingStep').textContent = 'Listo';
    setTimeout(() => { $('processing').hidden = true; }, 400);
    toast(total === 1 ? 'Repetición analizada' : `${total} repeticiones analizadas`);
  } catch (error) {
    $('processing').hidden = true;
    toast(`No se pudo analizar: ${error.message}`, true);
  }
}

if ($('dropZone')) {
  const zone = $('dropZone');
  zone.addEventListener('dragover', (event) => {
    event.preventDefault();
    zone.classList.add('is-over');
  });
  zone.addEventListener('dragleave', () => zone.classList.remove('is-over'));
  zone.addEventListener('drop', (event) => {
    event.preventDefault();
    zone.classList.remove('is-over');
    handleReplayFiles(event.dataTransfer?.files);
  });
  $('replayInput').addEventListener('change', (event) => {
    handleReplayFiles(event.target.files);
    event.target.value = '';
  });
}

// El botón de volver arriba aparece solo cuando la página ya se ha desplazado. Se usa un
// centinela al principio del documento porque el observador reacciona sin depender de que
// lleguen eventos de scroll (que se espacian cuando la pestaña está en segundo plano).
const toTop = $('toTop');
const sentinel = $('scrollSentinel');
const syncToTop = () => { toTop.hidden = window.scrollY < 420; };
window.addEventListener('scroll', syncToTop, { passive: true });
if (sentinel && 'IntersectionObserver' in window) {
  new IntersectionObserver(([entry]) => { toTop.hidden = entry.isIntersecting; }).observe(sentinel);
}
toTop.addEventListener('click', () => {
  window.scrollTo({ top: 0, behavior: 'smooth' });
});
syncToTop();

if ($('clearDataBtn')) {
  $('clearDataBtn').addEventListener('click', async () => {
    if (!window.confirm('¿Borrar de este navegador las repeticiones analizadas y sus resultados?')) {
      return;
    }

    await TTStore.clearAll();
    state.report = null;
    state.matches = [];
    state.files = [];
    await load();
    toast('Datos borrados');
  });
}

load();

// La web estática no tiene a quién preguntar: se refresca solo al volver a la pestaña.
setInterval(() => {
  if (!document.hidden && state.mode === 'server') {
    load();
  }
}, 10000);
