/*
 * Motor de análisis en el navegador.
 *
 * El mismo lector de repeticiones del exe, compilado a WebAssembly (.NET + Mono). Se carga
 * solo cuando hace falta (primera repetición o catálogo de premios), así la primera pantalla
 * aparece al instante.
 */
const TTEngine = (() => {
  let exportsPromise = null;

  async function runtime() {
    if (!exportsPromise) {
      exportsPromise = (async () => {
        // Ruta absoluta al runtime: este script vive en js/, no en la raíz.
        const dotnetUrl = new URL('_framework/dotnet.js', document.baseURI).href;
        const { dotnet } = await import(dotnetUrl);
        const runtime = await dotnet.withDiagnosticTracing(false).create();
        const config = runtime.getConfig();
        const exports = await runtime.getAssemblyExports(config.mainAssemblyName);
        return exports.FortnitePirateTokenTracker.Web.PanelExports;
      })();
    }

    return exportsPromise;
  }

  /** Uint8Array → base64 sin reventar la pila con archivos grandes. */
  function toBase64(buffer) {
    const bytes = new Uint8Array(buffer);
    const trozo = 0x8000;
    let binario = '';

    for (let i = 0; i < bytes.length; i += trozo) {
      binario += String.fromCharCode.apply(null, bytes.subarray(i, i + trozo));
    }

    return btoa(binario);
  }

  async function analyze(file) {
    const api = await runtime();
    const buffer = await file.arrayBuffer();
    return JSON.parse(api.Analyze(toBase64(buffer), file.name, true));
  }

  async function buildReport(matches, config) {
    const api = await runtime();
    return JSON.parse(api.BuildReport(JSON.stringify(matches ?? []), JSON.stringify(config ?? {})));
  }

  async function catalog() {
    const api = await runtime();
    return JSON.parse(api.Catalog());
  }

  return { runtime, analyze, buildReport, catalog };
})();
