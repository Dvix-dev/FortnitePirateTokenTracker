const state = {
  config: null,
  report: null,
  catalog: [],
  folderExists: false,
  replayCount: 0,
  paths: null
};

const slotLabels = {
  outfit: 'Outfit',
  pickaxe: 'Pico',
  backBling: 'Mochila',
  glider: 'Planeadora',
  loadingScreen: 'Pantalla de carga',
  musicPack: 'Música',
  pet: 'Mascota',
  wrap: 'Envoltorio'
};

const $ = (id) => document.getElementById(id);

function esc(value) {
  return String(value ?? '')
    .replaceAll('&', '&amp;')
    .replaceAll('<', '&lt;')
    .replaceAll('>', '&gt;')
    .replaceAll('"', '&quot;');
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

  const cards = [
    { label: 'Partidas analizadas', value: totals?.replaysAnalyzed ?? 0, sub: `${state.replayCount} archivos en la carpeta`, gold: false },
    { label: 'Cofres detectados', value: totals?.chestsDetected ?? 0, sub: `Media ${totals?.averageChestsPerMatch ?? 0} por partida`, gold: false },
    { label: 'Tokens míos', value: totals?.tokenChestsByMe ?? 0, sub: `${totals?.chestsByMe ?? 0} cofres abiertos por mí`, gold: true },
    { label: 'Tokens de otros', value: totals?.tokenChestsByOthers ?? 0, sub: 'En mis partidas', gold: false },
    { label: 'Partidas con Token', value: `${totals?.tokenRate ?? 0}%`, sub: `${totals?.matchesWithToken ?? 0} de ${totals?.replaysAnalyzed ?? 0}`, gold: true }
  ];

  $('statsGrid').innerHTML = cards.map((card) => `
    <div class="stat ${card.gold ? 'is-gold' : ''}">
      <div class="stat-label">${esc(card.label)}</div>
      <div class="stat-value">${esc(card.value)}</div>
      <div class="stat-sub">${esc(card.sub)}</div>
    </div>
  `).join('');

  $('tokenRatePill').textContent = totals ? `${totals.tokenRate ?? 0}% de partidas` : '—';
  $('rateNote').textContent = totals
    ? `${totals.matchesWithToken} partidas con Token · ${totals.matchesWithMultipleTokens} con más de uno.`
    : 'Analiza tus repeticiones para calcular la tasa real.';

  const max = Math.max(1, totals?.chestsDetected ?? 1);
  const rows = [
    { label: 'Cofres totales', value: totals?.chestsDetected ?? 0 },
    { label: 'Cofres míos', value: totals?.chestsByMe ?? 0 },
    { label: 'Tokens míos', value: totals?.tokenChestsByMe ?? 0 },
    { label: 'Tokens de otros', value: totals?.tokenChestsByOthers ?? 0 }
  ];

  $('rateBars').innerHTML = rows.map((row) => `
    <div class="bar-row">
      <div class="bar-head"><span>${esc(row.label)}</span><span>${esc(row.value)}</span></div>
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
    .filter((c) => !onlyTokens || c.isToken)
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
    return !raw || /cosmetics\.variant|immutable|sin identificar/i.test(raw);
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
    const label = raw && !/cosmetics\.variant|immutable|sin identificar/i.test(raw) ? raw : 'Tesoro del evento';
    const source = /dorado/i.test(c.containerClass || '') ? 'cofre dorado' : 'habilidad del evento';
    return `<li><strong>${esc(matchLabel(c.replayFile))}</strong> · ${esc(when)} de partida · ${esc(label)} · ${esc(who)} · ${esc(source)}</li>`;
  }).join('');

  const found = new Map((state.report?.categories ?? []).map((c) => [c.key, c.found]));
  $('prizeList').innerHTML = state.catalog.map((prize) => `
    <div class="prize">
      <div class="prize-thumb" style="background:linear-gradient(140deg, ${esc(prize.accent)}33, ${esc(prize.accent)}0d)">
        <img src="/img/${esc(prize.image)}" alt="${esc(prize.label)}" loading="lazy">
      </div>
      <div class="prize-body">
        <div class="prize-name">${esc(prize.label)}</div>
        <div class="prize-desc">${esc(prize.description)}</div>
      </div>
      <div class="prize-right">
        <div class="prize-odds">${esc(prize.odds)}</div>
        <div class="prize-found">${found.get(prize.key) ?? 0} encontrados</div>
      </div>
    </div>
  `).join('');
}

/* ---------------- cosméticos ---------------- */

function renderCosmetics() {
  const entries = state.report?.cosmetics ?? [];
  const grouped = new Map();

  for (const entry of entries) {
    const key = `${entry.isLocalPlayer ? 'yo' : ''}${entry.playerName}`;
    if (!grouped.has(key)) {
      grouped.set(key, { name: entry.playerName, isLocal: entry.isLocalPlayer, slots: [] });
    }
    if (entry.itemPath) {
      grouped.get(key).slots.push(entry);
    }
  }

  $('playersGrid').innerHTML = [...grouped.values()].map((player) => `
    <div class="player-card">
      <div class="player-head">
        <span>${esc(player.name)}</span>
        ${player.isLocal ? '<span class="tag tag-local">tú</span>' : ''}
      </div>
      <div class="slots">
        ${player.slots.map((slot) => `
          <div class="slot">
            <span class="slot-name">${esc(slotLabels[slot.slot] ?? slot.slot)}</span>
            <span class="slot-value">${esc(slot.itemPath)}</span>
          </div>
        `).join('') || '<span class="muted small">Sin cosméticos replicados</span>'}
      </div>
    </div>
  `).join('');

  $('cosmeticEmpty').hidden = grouped.size > 0;
}

/* ---------------- partidas ---------------- */

function renderMatches() {
  const matches = [...(state.report?.matches ?? [])].reverse();

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
        <span>Tokens<b>${esc(match.tokens)}</b></span>
      </div>
    </div>
  `).join('');

  $('matchesEmpty').hidden = matches.length > 0;
}

/* ---------------- mapa de calor ---------------- */

// Caja del mapa en píxeles de la imagen original y extensión del mundo que cubre.
// Las celdas son las mismas de 50 m (5.000 unidades) que usa el informe.
const MAP_IMG = { w: 560, h: 555, left: 30, top: 1, right: 520, bottom: 511 };
const MAP_WORLD = { xMin: -60000, xMax: 94000, yMin: -62000, yMax: 98000 };
const MAP_CELL = 5000;

function mapPoint(x, y) {
  const w = MAP_IMG.right - MAP_IMG.left;
  const h = MAP_IMG.bottom - MAP_IMG.top;
  const nx = (x - MAP_WORLD.xMin) / (MAP_WORLD.xMax - MAP_WORLD.xMin);
  const ny = (y - MAP_WORLD.yMin) / (MAP_WORLD.yMax - MAP_WORLD.yMin);
  return { x: MAP_IMG.left + nx * w, y: MAP_IMG.bottom - ny * h };
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

  const onlyTreasures = $('mapOnlyTreasures').checked;
  const chests = state.report?.chests ?? [];
  const cells = new Map();
  let placed = 0;
  let total = 0;

  for (const chest of chests) {
    if (onlyTreasures && !/tesoro|dorado/i.test(chest.containerClass || '')) continue;
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
  const max = Math.max(2, counts.length ? counts[Math.floor(counts.length * 0.95)] : 2);
  const weight = (n) => Math.log1p(n) / Math.log1p(max);

  for (const [key, n] of cells) {
    const [cx, cy] = key.split(':').map(Number);
    const a = mapPoint(cx * MAP_CELL, cy * MAP_CELL);
    const b = mapPoint((cx + 1) * MAP_CELL, (cy + 1) * MAP_CELL);
    const x = Math.min(a.x, b.x) + 1;
    const y = Math.min(a.y, b.y) + 1;
    const w = Math.abs(b.x - a.x) - 2;
    const h = Math.abs(b.y - a.y) - 2;
    if (w <= 0 || h <= 0) continue;

    // Las celdas con uno o dos cofres (posiciones aproximadas en la costa) se aclaran para que
    // no tapen el mapa como si fueran zonas cargadas.
    const t = weight(n);
    ctx.fillStyle = heatColor(t, 0.24 + 0.5 * t);
    ctx.beginPath();
    ctx.roundRect(x, y, w, h, 3);
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

  const missing = onlyTreasures ? total - placed : 0;
  $('mapNote').textContent = onlyTreasures
    ? `${placed} de ${total} tesoros traen posición; los otros ${missing} llegan por la habilidad sin coordenadas, así que no se pueden situar. Las zonas se cuentan con las mismas celdas de 50 m del informe.`
    : `${placed.toLocaleString('es-ES')} aperturas situadas en celdas de 50 m. Más rojo = más cofres; los puntos blancos son tesoros con posición conocida.`;
}

window.addEventListener('resize', () => renderMap());

/* ---------------- zonas ---------------- */

function renderZones() {
  const zones = state.report?.zones ?? [];

  $('zonesList').innerHTML = zones.map((zone) => `
    <div class="zone">
      <span class="zone-name">${esc(zone.name)}</span>
      <span class="tag">${esc(zone.chests)} cofres</span>
      <span class="tag ${zone.tokens > 0 ? 'tag-local' : ''}">${esc(zone.tokens)} tokens</span>
      <span class="muted mono">${Math.round(zone.x)}, ${Math.round(zone.y)}</span>
    </div>
  `).join('');

  $('zonesEmpty').hidden = zones.length > 0;
  if (zones.length === 0) {
    $('zonesEmpty').textContent = 'Sin coordenadas registradas todavía. Los cofres sin posición replicada no se agrupan.';
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
    `${opened} míos abiertos · ${broken} rotos por mí · ${mates} de mi squad · ${otros} de otros jugadores.`;
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
  renderCosmetics();
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
$('mapOnlyTreasures').addEventListener('change', renderMap);

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

$('scanBtn').addEventListener('click', openScanModal);

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
    const data = await api('/api/state');
    Object.assign(state, data);
    state.loadedAt = new Date().toLocaleTimeString('es-ES');
    renderAll();
  } catch (error) {
    toast(`No se pudo cargar el panel: ${error.message}`, true);
  }
}

load();

// Se refresca solo: al terminar un análisis o al aparecer repes nuevas, la lista de eventos
// cambia sin recargar la página.
setInterval(() => {
  if (!document.hidden) {
    load();
  }
}, 10000);
