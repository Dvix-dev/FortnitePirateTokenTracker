/*
 * Almacenamiento del panel.
 *
 * Hay dos modos y el panel funciona igual en los dos:
 *   - «servidor»: lo sirve TokenTracker.exe en localhost y los datos viven en `data/`.
 *   - «navegador»: web estática; el informe vive en IndexedDB, dentro de tu equipo.
 *
 * Ningún dato sale del navegador en el modo estático: no hay peticiones de subida.
 */
const TTStore = (() => {
  const DB_NAME = 'tesoro-tracker';
  const DB_VERSION = 1;
  const STORE = 'panel';

  let mode = null;
  let dbPromise = null;

  function openDb() {
    if (!dbPromise) {
      dbPromise = new Promise((resolve, reject) => {
        const request = indexedDB.open(DB_NAME, DB_VERSION);
        request.onupgradeneeded = () => {
          const db = request.result;
          if (!db.objectStoreNames.contains(STORE)) {
            db.createObjectStore(STORE);
          }
        };
        request.onsuccess = () => resolve(request.result);
        request.onerror = () => reject(request.error);
      });
    }
    return dbPromise;
  }

  async function read(key) {
    const db = await openDb();
    return new Promise((resolve, reject) => {
      const tx = db.transaction(STORE, 'readonly');
      const request = tx.objectStore(STORE).get(key);
      request.onsuccess = () => resolve(request.result ?? null);
      request.onerror = () => reject(request.error);
    });
  }

  async function write(key, value) {
    const db = await openDb();
    return new Promise((resolve, reject) => {
      const tx = db.transaction(STORE, 'readwrite');
      tx.objectStore(STORE).put(value, key);
      tx.oncomplete = () => resolve();
      tx.onerror = () => reject(tx.error);
    });
  }

  async function drop(key) {
    const db = await openDb();
    return new Promise((resolve, reject) => {
      const tx = db.transaction(STORE, 'readwrite');
      tx.objectStore(STORE).delete(key);
      tx.oncomplete = () => resolve();
      tx.onerror = () => reject(tx.error);
    });
  }

  /** ¿Nos sirve el exe local o estamos en una web estática? */
  async function detect() {
    if (mode) {
      return mode;
    }

    try {
      const response = await fetch('api/state', { headers: { Accept: 'application/json' }, cache: 'no-store' });
      if (response.ok) {
        const data = await response.json();
        if (data && Object.prototype.hasOwnProperty.call(data, 'report')) {
          mode = 'server';
          return mode;
        }
      }
    } catch {
      // Sin API: es una web estática. Es lo normal cuando se sube a un hosting.
    }

    mode = 'local';
    return mode;
  }

  async function load() {
    const current = await detect();

    if (current === 'server') {
      const response = await fetch('api/state', { cache: 'no-store' });
      const data = await response.json();
      return { ...data, mode: current, matches: [] };
    }

    const [report, matches, files, catalog] = await Promise.all([
      read('report'),
      read('matches'),
      read('files'),
      read('catalog')
    ]);

    return {
      mode: current,
      config: null,
      report: report ?? null,
      matches: matches ?? [],
      files: files ?? [],
      catalog: catalog ?? [],
      replayCount: (files ?? []).length,
      folderExists: false,
      paths: null,
      version: '0.3.0'
    };
  }

  /** Guarda el resultado de una repetición ya analizada en el navegador. */
  async function addMatch(fileName, match) {
    const [files, matches] = await Promise.all([read('files'), read('matches')]);
    const listaArchivos = files ?? [];
    const listaPartidas = matches ?? [];

    if (!listaArchivos.includes(fileName)) {
      listaArchivos.push(fileName);
    }
    listaPartidas.push(match);

    await write('files', listaArchivos);
    await write('matches', listaPartidas);
    return listaPartidas;
  }

  const saveReport = (report) => write('report', report);
  const saveCatalog = (catalog) => write('catalog', catalog);

  async function clearAll() {
    await Promise.all(['report', 'matches', 'files'].map(drop));
  }

  return {
    detect,
    load,
    addMatch,
    saveReport,
    saveCatalog,
    clearAll,
    get mode() {
      return mode;
    }
  };
})();
