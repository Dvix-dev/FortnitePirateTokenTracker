# Publicar la web: publicidad, servidor, subdominio y visitas

Guía de puesta en producción del panel estático (`dist-web/`) con anuncios, un subdominio propio
detrás del proxy de Cloudflare y un plan para atraer visitantes.

> Resumen en una línea: `bash tools/build-web.sh` → subir el contenido de `dist-web/` a una carpeta
> del servidor → crear el registro DNS `A` del subdominio en Cloudflare con la nube naranja →
> SSL/TLS en **Full (strict)** con un certificado de origen → pegar los anuncios en los `aside`
> reservados → enviar el sitemap a Search Console.

---

## 0. Checklist rápido

- [ ] `bash tools/build-web.sh` termina sin errores y `dist-web/` pesa ~6,5 MB.
- [ ] Probado en local con `python -m http.server 8099 --directory dist-web`.
- [ ] Dominio definitivo decidido (por ejemplo `tesoros.tudominio.com`).
- [ ] `canonical`, `og:url`, `robots.txt` y `sitemap.xml` apuntan a ese dominio.
- [ ] Contenido subido y sirviendo `.wasm` como `application/wasm`.
- [ ] DNS del subdominio en Cloudflare (nube naranja) y SSL en Full (strict).
- [ ] Redirección 301 del dominio viejo/apex al subdominio nuevo.
- [ ] Anuncios pegados, `ads.txt` publicado y aviso de privacidad actualizado.
- [ ] Sitemap enviado a Google Search Console y Bing Webmaster Tools.

---

## 1. Generar y probar el paquete

```bash
bash tools/build-web.sh
```

Genera `dist-web/` con el panel (`index.html`, `app.css`, `app.js`, `js/`, `img/`, `fonts/`,
`robots.txt`, `sitemap.xml`) y el motor .NET compilado a WebAssembly en `_framework/`.

Antes de subir nada:

```bash
python -m http.server 8099 --bind 127.0.0.1 --directory dist-web
# http://localhost:8099
```

Comprueba que:

1. Arrastras un `.replay` real y termina el análisis (una repetición de 20 MB tarda 30–90 s).
2. Recargas la página y los datos siguen ahí (IndexedDB).
3. La consola del navegador no muestra errores 404 ni de MIME.

**Qué NO subir:** `data/`, `.dotnet/`, `bin/`, `obj/`, el `.exe`, ni el código fuente. Solo el
contenido de `dist-web/`.

---

## 2. Publicidad

### 2.1 Dónde están los huecos

El panel ya trae los contenedores y el CSS les da altura mínima para que el diseño no salte
mientras carga el anuncio:

| Hueco | Cuántos | Dónde | Cuándo se ve |
|-------|---------|-------|--------------|
| `aside.ad-slot` | 7 | Uno por pestaña: Resumen, Cofres, Premios, Partidas, Zonas, Mis datos y Guía | Siempre, dentro del flujo |
| `aside.ad-rail.ad-rail-left` / `-right` | 2 | Columna izquierda y derecha, fijas | Solo con ventana ≥ 1480 px |

Están en `src/FortnitePirateTokenTracker.App/wwwroot/index.html`. Búscalos por la cadena
`Espacio para publicidad`. Los raíles están al final del documento, justo antes de `<div class="toast">`.

Medidas actuales: `.ad-slot` ocupa el ancho del panel con 92 px de alto mínimo; `.ad-rail` mide
160 px de ancho y 600 px de alto mínimo, y empieza a 140 px del borde superior para no pisar la
cabecera ni las pestañas. Si tu red exige 300×600 en los laterales, cambia en `app.css`:

```css
.ad-rail { width: 300px; }
.ad-rail-left  { left:  max(16px, calc(50% - 830px)); }
.ad-rail-right { right: max(16px, calc(50% - 830px)); }
```

…y sube el `@media` de 1480 px a ~1720 px para que solo aparezcan cuando de verdad sobran 300 px
por lado. El resto del panel mide 1080 px como máximo.

### 2.2 Elegir red

- **Google AdSense**: la opción realista para empezar. Gratis, requiere aprobación y una política
  de privacidad pública. Paga poco con tráfico bajo, pero no exige mínimos.
- **Ezoic / Mediavine / Raptive**: piden decenas de miles de sesiones al mes. No aplican al principio.
- **Patrocinio directo o afiliados**: un `<a>` con una imagen dentro del `aside` y un enlace a una
  tienda de periféricos, sillas o monitores. Cero requisitos de tráfico y suele pagar mejor al
  principio. Si eliges esto, mantén el mismo `aside` y añade `rel="sponsored noopener"`.

### 2.3 Insertar AdSense paso a paso

1. Crea la cuenta en <https://adsense.google.com> con el dominio ya publicado y con contenido.
2. Añade el sitio (`Sites → Add site`) con la URL exacta, por ejemplo `tesoros.tudominio.com`.
3. Verifica la propiedad. Lo más cómodo con Cloudflare: **DNS TXT** con el valor que te da AdSense
   (DNS → Records → Add record → TXT, nombre `@`). La alternativa es pegar su script en el `<head>`.
4. Espera la aprobación (de horas a dos semanas). Mientras tanto el sitio debe estar accesible, sin
   páginas vacías y con la política de privacidad enlazada.
5. Crea las unidades: `Ads → By ad unit → Display ads`. Anota `data-ad-client` (`ca-pub-…`) y el
   `data-ad-slot` de cada unidad.
6. Pega **una unidad por `aside`**. Ejemplo para un hueco del flujo:

```html
<aside class="ad-slot" aria-label="Publicidad">
  <ins class="adsbygoogle"
       style="display:block"
       data-ad-client="ca-pub-0000000000000000"
       data-ad-slot="1234567890"
       data-ad-format="auto"
       data-full-width-responsive="true"></ins>
  <script>(adsbygoogle = window.adsbygoogle || []).push({});</script>
</aside>
```

Para los laterales, usa una unidad vertical y deja que el CSS marque el tamaño:

```html
<aside class="ad-rail ad-rail-left" aria-label="Publicidad lateral">
  <ins class="adsbygoogle"
       style="display:block;width:160px;height:600px"
       data-ad-client="ca-pub-0000000000000000"
       data-ad-slot="0987654321"></ins>
  <script>(adsbygoogle = window.adsbygoogle || []).push({});</script>
</aside>
```

7. El script global de AdSense va **una sola vez**, en el `<head>` del `index.html`:

```html
<script async src="https://pagead2.googlesyndication.com/pagead/js/adsbygoogle.js?client=ca-pub-0000000000000000"
        crossorigin="anonymous"></script>
```

No repitas ese `<script>` por cada unidad: solo el `push({})`.

### 2.4 `ads.txt`

AdSense no funciona bien sin él. Créalo en la raíz del sitio (`dist-web/ads.txt`) con una línea:

```
google.com, pub-0000000000000000, DIRECT, f08c47fec0942fa0
```

Y comprueba que responde en `https://tu-subdominio/ads.txt`.

### 2.5 Privacidad, consentimiento y confianza

La promesa del panel («las repeticiones no se suben») sigue siendo cierta, pero **la red
publicitaria sí hace peticiones y puede usar cookies**. Hay que decirlo:

1. Actualiza los textos de privacidad: `.dropzone-privacy`, `.processing-privacy`,
   `#privacyFooter` y la sección de privacidad del `README.md`. Añade una frase del tipo:
   «La publicidad la sirve un tercero que puede usar cookies propias; tus repeticiones siguen sin
   salir del navegador».
2. Publica una página de privacidad accesible (por ejemplo `privacidad.html` enlazada desde el
   pie). AdSense la exige y también ayuda a la aprobación.
3. Para visitantes de la UE/Reino Unido/Suiza, AdSense exige una **CMP certificada** (la propia de
   Google en `Privacy & messaging`, o Cookiebot). Configúrala antes de activar anuncios.
4. No pongas anuncios que se superpongan al contenido ni al botón de subir arriba: AdSense penaliza
   la colocación engañosa y el panel ya tiene los huecos pensados para ello.
5. Carga diferida: el análisis de la repetición bloquea el hilo principal 30–90 s. Si los anuncios
   se cargan justo en ese momento, la petición se retrasa y puedes perder impresiones. Deja que
   carguen al abrir la página (como en los ejemplos) y no los metas dentro de la pantalla de carga.

### 2.6 Comprobar que la publicidad no rompe nada

- Redimensiona a 1480 px y a 1479 px: los raíles deben aparecer y desaparecer sin tapar el panel.
- Con anuncios cargados, las pestañas deben seguir por encima (`.topbar` tiene `z-index: 20`).
- Revisa `Network` y la consola: sin errores de `adsbygoogle` ni de `ads.txt`.
- En móvil, oculta los laterales (ya lo hace el CSS) y deja solo los anuncios del flujo.

---

## 3. Subir al servidor

### 3.1 Qué necesita el hosting

No hace falta PHP, Node ni base de datos: son archivos estáticos.

- HTTPS obligatorio (WebAssembly no arranca desde `file://` ni desde `http://` en algunos navegadores).
- Tipos MIME: `.wasm` → `application/wasm`, `.js` → `text/javascript`, `.json` →
  `application/json`, `.woff2` → `font/woff2`, `.webp` → `image/webp`.
- Compresión gzip o Brotli (el runtime baja de ~5 MB a ~2 MB).
- Caché: `_framework/*` un año (los nombres llevan hash), `index.html` sin caché.

### 3.2 Subir los archivos

Con `rsync` (lo más cómodo y repetible):

```bash
rsync -avz --delete dist-web/ usuario@tu-servidor:/var/www/tesoros/
```

Con FTP/FileZilla: entra en la carpeta destino y sube **el contenido** de `dist-web/`, no la carpeta.

Permisos típicos en un servidor propio:

```bash
sudo chown -R www-data:www-data /var/www/tesoros
sudo find /var/www/tesoros -type d -exec chmod 755 {} \;
sudo find /var/www/tesoros -type f -exec chmod 644 {} \;
```

Si es un hosting compartido, la carpeta destino suele ser `public_html/` o `www/`.

### 3.3 Apache (`.htaccess`)

Si el hosting es Apache y no puedes tocar la configuración global, crea `dist-web/.htaccess`
antes de subir:

```apache
AddType application/wasm .wasm
AddType text/javascript .js
AddType application/json .json
AddType font/woff2 .woff2
AddType image/webp .webp

<IfModule mod_deflate.c>
  AddOutputFilterByType DEFLATE text/html text/css application/javascript application/json application/wasm image/svg+xml
</IfModule>

<IfModule mod_headers.c>
  <FilesMatch "\.(wasm|dll|dat|js|json|woff2)$">
    Header set Cache-Control "public, max-age=31536000, immutable"
  </FilesMatch>
  <FilesMatch "index\.html$">
    Header set Cache-Control "no-cache"
  </FilesMatch>
</IfModule>
```

En WAMP, además, activa `mod_headers`, `mod_deflate` y `mod_mime` en `httpd.conf` y reinicia.

### 3.4 Nginx

```nginx
server {
    listen 443 ssl http2;
    server_name tesoros.tudominio.com;
    root /var/www/tesoros;
    index index.html;

    # Certificado de origen de Cloudflare (o Let's Encrypt)
    ssl_certificate     /etc/ssl/cloudflare/origin.pem;
    ssl_certificate_key /etc/ssl/cloudflare/origin.key;

    types { application/wasm wasm; }
    gzip on;
    gzip_types text/css application/javascript application/json application/wasm image/svg+xml;

    location /_framework/ {
        add_header Cache-Control "public, max-age=31536000, immutable";
        try_files $uri =404;
    }

    location = /index.html {
        add_header Cache-Control "no-cache";
    }

    location / {
        try_files $uri $uri/ =404;
    }
}

server {
    listen 80;
    server_name tesoros.tudominio.com;
    return 301 https://$host$request_uri;
}
```

Recarga con `sudo nginx -t && sudo systemctl reload nginx`.

### 3.5 Verificar la subida

```bash
curl -I https://tesoros.tudominio.com/
curl -I https://tesoros.tudominio.com/_framework/dotnet.js | grep -i content-type
curl -I https://tesoros.tudominio.com/_framework/dotnet.native.wasm | grep -i -E "content-type|content-encoding"
```

Debe responder `200`, el `.wasm` con `application/wasm` y, si el navegador envía
`Accept-Encoding: br`, `content-encoding: br` o `gzip`.

---

## 4. Subdominio y SSL con Cloudflare

### 4.1 Crear el subdominio

1. En Cloudflare, entra en el dominio y ve a **DNS → Records → Add record**.
2. Tipo **A**, nombre `tesoros` (queda `tesoros.tudominio.com`), contenido: la IP pública del
   servidor. Si tienes IPv6, añade también un **AAAA**.
3. Deja la **nube naranja activada** (Proxied). Así Cloudflare pone el certificado de borde y
   oculta la IP.
4. Si el dominio no está en Cloudflare, primero añade el dominio (nameservers) y espera la
   propagación.

Comprueba:

```bash
dig +short tesoros.tudominio.com
```

Verás IPs de Cloudflare (`104.x`, `172.67.x`…), no la de tu servidor. Es lo esperado.

### 4.2 SSL/TLS

En **SSL/TLS → Overview** elige **Full (strict)**:

- El visitante ve el certificado de Cloudflare (gratis, renovado solo).
- Cloudflare habla con tu servidor por HTTPS y **valida** su certificado.

Para que Full (strict) funcione, el origen necesita un certificado válido para el subdominio:

- **Cloudflare Origin Certificate** (recomendado): SSL/TLS → Origin Server → Create Certificate,
  cubre `*.tudominio.com` y `tudominio.com`, 15 años. Instálalo en el servidor (los `.pem` del
  ejemplo de Nginx) y reinicia.
- **Let's Encrypt** con Certbot: también válido, pero recuerda renovarlo.

No uses **Flexible** salvo que el origen no pueda tener certificado: provoca bucles de redirección
si el servidor fuerza HTTPS y deja el tramo Cloudflare→origen sin cifrar.

Ajustes recomendados en **SSL/TLS → Edge Certificates**:

- **Always Use HTTPS**: ON.
- **Minimum TLS Version**: 1.2.
- **Automatic HTTPS Rewrites**: ON.
- **HSTS**: actívalo solo cuando todo funcione por HTTPS; empieza con 6 meses y sin `preload`.

### 4.3 Redirecciones (301)

Si el subdominio es la dirección definitiva, redirige a él el dominio principal y `www`:

**Rules → Redirect Rules → Create rule**

- Nombre: `apex → tesoros`.
- Si: `Custom filter expression` → `http.host eq "tudominio.com"` (y otra regla para `www`).
- Entonces: `Static redirect` → `https://tesoros.tudominio.com` + `Preserve query string`.
- Código: **301**.

Si el subdominio es solo un alias y prefieres que la web viva en el dominio principal, haz lo
contrario (redirige el subdominio al apex) y usa ese dominio en el `canonical`.

### 4.4 Actualizar el dominio en el código

Hoy hay marcadores `https://tesoro-tracker.example/`. Cámbialos **antes** de compilar:

| Archivo | Qué cambiar |
|---------|-------------|
| `src/FortnitePirateTokenTracker.App/wwwroot/index.html` | `<link rel="canonical">` y `og:url` |
| `src/FortnitePirateTokenTracker.App/wwwroot/robots.txt` | línea `Sitemap:` |
| `src/FortnitePirateTokenTracker.App/wwwroot/sitemap.xml` | `<loc>` |

Después vuelve a ejecutar `bash tools/build-web.sh` y sube el resultado. Si cambias de dominio más
adelante, repite estos tres pasos y purga la caché de Cloudflare.

### 4.5 Caché de Cloudflare

- **Caching → Configuration**: nivel estándar. Cloudflare ya respeta el `Cache-Control` del origen.
- Tras cada despliegue: **Caching → Purge Cache → Purge Everything** (o solo `index.html`,
  `app.css`, `app.js`).
- Para ver cambios al instante mientras pruebas: **Caching → Development Mode** (3 horas).
- No pongas en caché `index.html`: si se queda pegado, los usuarios no ven la versión nueva.

### 4.6 Verificación final

```bash
curl -I https://tudominio.com            # 301 a https://tesoros.tudominio.com/
curl -I https://tesoros.tudominio.com/   # 200
```

En el navegador: candado válido, sin contenido mixto en la consola, el análisis de una repetición
funciona y los anuncios cargan. Revisa también `https://tesoros.tudominio.com/ads.txt`.

---

## 5. Conseguir visitantes

La web es una sola página con pestañas. Eso está bien para la herramienta, pero Google indexa una
URL; para atraer tráfico conviene apoyarla con contenido y con comunidades.

### 5.1 Lo básico de SEO (1 hora)

1. **Google Search Console**: añade la propiedad (`Domain` o `URL prefix`), verifica por DNS TXT en
   Cloudflare y envía `https://tesoros.tudominio.com/sitemap.xml`.
2. **Bing Webmaster Tools**: importa el sitio desde Search Console; es un clic.
3. **Imagen social**: `img/og.png` a 1200×630. Es lo que se ve al compartir en Discord, X o WhatsApp.
4. **Título y descripción**: ya están en el `<head>`; si cambias el enfoque, que lleven la palabra
   que buscaría la gente («mapa de cofres de Fortnite», «analizar repeticiones», «tesoros»).
5. **Contenido indexable**: añade 2–3 páginas estáticas enlazadas desde el panel, por ejemplo
   `guias/como-ver-tus-repeticiones.html`, `guias/mapa-de-cofres.html` y `privacidad.html`. Cada una
   con texto propio, captura y enlace a la herramienta. Son las que pueden posicionar y también dan
   sitio para anuncios sin ensuciar la app.
6. **Datos estructurados**: el bloque JSON-LD ya declara `WebApplication` y `Offer`. Cuando tengas
   guías, añade `FAQPage` a la de preguntas frecuentes.

### 5.2 Lanzamiento en comunidades

- **Reddit**: `r/FortNiteBR` y `r/FortniteBR` tienen reglas estrictas contra la autopromoción; usa los
  hilos semanales de contenido/discusión y responde dudas antes de enlazar. Un post tipo «hice una
  herramienta que analiza tus repeticiones y te dice dónde salieron los tesoros» con una captura del
  mapa funciona mejor que un enlace pelado.
- **Discord** de creadores, servidores de Fortnite y de análisis de repeticiones: comparte el enlace
  cuando alguien pregunte por cofres o tesoros, no en spam masivo.
- **TikTok / YouTube Shorts**: 15–30 s mostrando el mapa de calor y el «1 tesoro en 337 cofres».
  Ese dato sorprende y genera clics. Enlace en la bio.
- **X/Twitter**: hilo corto con dos capturas (mapa y tabla de partidas) y el enlace al final.
- **Comunidades hispanas**: foros y servidores de Fortnite en español suelen tener menos competencia
  que los ingleses.

### 5.3 Medir

- **Cloudflare Web Analytics** (gratis, sin cookies, un script en el `<head>`): visitas, referrers y
  páginas vistas sin tocar la privacidad del panel.
- Alternativas: Plausible o Umami autoalojado.
- Mira tres cosas: visitas, cuántos **arrastran una repetición** y cuántos vuelven. Si mucha gente
  entra y no analiza nada, el problema es el mensaje de la portada o el tiempo de proceso, no el SEO.

### 5.4 Legal y marca

- Deja claro en el pie que es un proyecto **no oficial** y sin relación con Epic Games.
- No uses logotipos oficiales de Fortnite ni capturas con datos de otros jugadores.
- Cumple la política de contenido de fans de Epic y las normas de AdSense sobre contenido de
  terceros.
- No publiques `data/` ni repeticiones de terceros: aunque el panel no las sube, el repositorio y el
  servidor tampoco deben tenerlas.

---

## 6. Mantenimiento

- Cada cambio: `bash tools/build-web.sh` → `rsync` → purgar caché de Cloudflare.
- Guarda una copia del `dist-web/` que funciona (por ejemplo `dist-web-2026-11-19/`) antes de
  actualizar, por si hay que volver atrás.
- Revisa una vez al mes: certificado de origen, renovación de Let's Encrypt (si lo usas), aprobación
  de anuncios y errores en Search Console.
