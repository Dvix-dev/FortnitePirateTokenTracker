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

function renderChests() {
  const onlyTokens = $('onlyTokens').checked;
  const chests = (state.report?.chests ?? []).filter((c) => !onlyTokens || c.isToken);
  const body = $('chestTable').querySelector('tbody');

  body.innerHTML = chests.slice(0, 400).map((c) => `
    <tr>
      <td class="mono">${esc(c.replayFile)}</td>
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

  $('chestEmpty').hidden = chests.length > 0;
  $('chestEmpty').textContent = state.report
    ? 'No hay cofres que coincidan con el filtro.'
    : 'Sin cofres registrados todavía. Analiza tus repeticiones desde el botón «Analizar ahora».';
}

/* ---------------- premios ---------------- */

function renderPrizes() {
  const treasures = (state.report?.chests ?? [])
    .filter((c) => /tesoro|dorado/i.test(c.containerClass || ''))
    .sort((a, b) => (b.timeMs ?? 0) - (a.timeMs ?? 0));

  $('treasureCount').textContent = treasures.length;
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
    const label = raw && !/cosmetics\.variant|immutable/i.test(raw) ? raw : 'premio sin identificar';
    const source = /dorado/i.test(c.containerClass || '') ? 'cofre dorado' : 'habilidad del evento';
    return `<li><strong>${esc(label)}</strong> · ${esc(who)} · ${when} · ${esc(source)} · ${esc((c.replayFile || '').replace('.replay', ''))}</li>`;
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
        <div class="match-name">${esc(match.replayFile)}</div>
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

  let total = 0;
  let opened = 0;
  let broken = 0;
  let mates = 0;

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
    }
  }

  $('myChestsSum').textContent = total.toLocaleString('es-ES');
  $('myChestsNote').textContent =
    `${opened} míos abiertos · ${broken} rotos por mí · ${mates} de mi squad.`;
}

['countMineOpened', 'countMineDestroyed', 'countSquad'].forEach((id) => {
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
    renderAll();
  } catch (error) {
    toast(`No se pudo cargar el panel: ${error.message}`, true);
  }
}

load();
