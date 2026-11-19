#!/usr/bin/env bash
# Genera `dist-web/`: el panel estático con el motor WebAssembly dentro.
# Sube el CONTENIDO de esa carpeta a tu hosting (ESModules y .wasm con su tipo MIME).
set -euo pipefail

cd "$(dirname "$0")/.."

DOTNET="${DOTNET:-./.dotnet/dotnet.exe}"
PROJECT="src/FortnitePirateTokenTracker.Web/FortnitePirateTokenTracker.Web.csproj"
BUNDLE="src/FortnitePirateTokenTracker.Web/bin/Release/net10.0/browser-wasm/AppBundle"
PANEL="src/FortnitePirateTokenTracker.App/wwwroot"
OUT="dist-web"

echo "Compilando el motor a WebAssembly…"
"$DOTNET" publish "$PROJECT" -c Release

if [ ! -d "$BUNDLE/_framework" ]; then
  echo "No se encontró el bundle en $BUNDLE" >&2
  exit 1
fi

echo "Montando $OUT…"
rm -rf "$OUT"
mkdir -p "$OUT"
cp -r "$BUNDLE"/. "$OUT"/
cp -r "$PANEL"/. "$OUT"/

echo
echo "Listo. Sube el contenido de $OUT a tu hosting."
echo "Se sirve por HTTP(S): abrir el index.html como archivo local no funciona (WebAssembly necesita origen)."
