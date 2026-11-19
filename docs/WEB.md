# Publicar la web estática

El panel funciona de dos maneras y **no hay dos proyectos que mantener**:

| Modo | Cómo se detecta | Dónde se guardan los datos |
|------|-----------------|-----------------------------|
| Local | `TokenTracker.exe` sirve `api/state` | `data/` junto al ejecutable |
| Web | no hay `api/state` | IndexedDB del navegador |

El panel pregunta una sola vez a `api/state`: si responde, está en local; si da 404, entra en
modo web y muestra la zona de arrastre. Nada más que configurar.

## 1. Generar la carpeta

```bash
bash tools/build-web.sh
```

Produce `dist-web/` con:

- `index.html`, `app.css`, `app.js`, `js/`, `img/`, `fonts/` — el panel
- `_framework/` — el motor .NET compilado a WebAssembly

Sube **el contenido** de `dist-web/`, no la carpeta.

## 2. Requisitos del hosting

- Servir por **HTTPS** (WebAssembly no arranca desde `file://`).
- Tipos MIME correctos: `.wasm` → `application/wasm`, `.js` → `text/javascript`.
  Apache, Nginx, Netlify, Cloudflare Pages, Vercel y GitHub Pages ya los traen.
- Si publicas en un subdirectorio (por ejemplo `usuario.github.io/tesoro/`), funciona igual:
  todas las rutas del panel son relativas.

Prueba en local antes de subir nada:

```bash
python -m http.server 8099 --directory dist-web
# http://localhost:8099
```

## 3. Personalizar antes de publicar

1. **Dominio**: en `src/FortnitePirateTokenTracker.App/wwwroot/index.html` cambia
   `https://tesoro-tracker.example/` en `<link rel="canonical">` y en `og:url`.
   Haz lo mismo en `robots.txt` y `sitemap.xml`.
2. **Textos SEO**: `<title>`, `description`, `keywords`, `og:*` y el bloque JSON-LD están al
   principio del `index.html`.
3. **Imagen social**: `img/og.png` (1200×630).
4. **Publicidad**: hay dos huecos marcados con `class="ad-slot"` en `index.html`. Sustituye su
   contenido por el script de tu red (AdSense, Ezoic…). El CSS les da una altura mínima para
   que no salte el diseño mientras cargan.

## 4. Qué se puede prometer (y qué no)

- Las repeticiones **no se suben**: se leen con `File.arrayBuffer()` y se procesan en la pestaña.
- Los resultados viven en IndexedDB; el botón «Borrar mis datos» de la pestaña *Mis datos* los elimina.
- No hay analítica ni peticiones a terceros en el código del panel. Si añades publicidad, esa red
  sí hará sus propias peticiones: conviene avisarlo en el aviso de privacidad.

## 5. Rendimiento

- Primera visita: descarga ~5 MB de runtime (queda en caché del navegador).
- Una repetición de 20 MB: 30–90 s en un portátil normal. Se procesa en el hilo principal, así que
  la pantalla de carga permanece visible y la pestaña no responde hasta terminar.
- Varias repeticiones: se encolan; el informe se recalcula una sola vez al final.
