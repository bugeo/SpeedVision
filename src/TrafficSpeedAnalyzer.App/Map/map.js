// Calibrazione «sulla mappa»: l'utente trova il luogo della ripresa, clicca dove la linea A e la linea B
// incontrano lo stesso bordo della strada e la distanza tra i due punti diventa la distanza tra le linee.
// Comunica con il programma (MapCalibrationWindow) tramite i messaggi di WebView2.
(function () {
  'use strict';

  const host = window.chrome && window.chrome.webview;
  const post = m => { if (host) host.postMessage(m); window.__tsaLastMessage = m; };

  /** Sotto questo ingrandimento un clic avvicina la mappa invece di mettere il punto. */
  const MIN_PICK_ZOOM = 18;
  /** Errore tipico di un clic, in pixel dello schermo. */
  const CLICK_ERROR_PX = 2;

  let S = {};
  let map = null, layers = null, currentLayer = 'map';
  let a = null, b = null;               // { lat, lon, mpp }
  let markerA = null, markerB = null, link = null;
  let found = false;                   // luogo trovato (posizione del video o ingrandimento sufficiente)
  let language = 'it';
  let numberFormat = new Intl.NumberFormat('it-IT', { minimumFractionDigits: 2, maximumFractionDigits: 2 });

  const $ = id => document.getElementById(id);

  function text(key, ...args) {
    let s = S[key] ?? key;
    args.forEach((v, i) => { s = s.replace('{' + i + '}', v); });
    return s;
  }

  function fmt(v, digits = 2) {
    return new Intl.NumberFormat(language, { minimumFractionDigits: digits, maximumFractionDigits: digits }).format(v);
  }

  // ───────────── Avvio (dati dal programma) ─────────────

  function init(data) {
    S = data.strings || {};
    language = data.language || 'it';
    numberFormat = new Intl.NumberFormat(language, { minimumFractionDigits: 2, maximumFractionDigits: 2 });
    document.documentElement.lang = language;
    document.querySelectorAll('[data-s]').forEach(el => { el.textContent = text(el.dataset.s); });
    document.title = text('title');
    $('search-text').placeholder = text('searchPlaceholder');
    $('search-button').textContent = text('search');
    $('use').textContent = text('use');
    $('reset').textContent = text('reset');
    $('cancel').textContent = text('cancel');
    if (data.snapshot) {
      $('snapshot').src = data.snapshot;
      $('snapshot-box').open = true;
    } else {
      $('snapshot-box').hidden = true;
    }

    const osm = L.tileLayer('https://tile.openstreetmap.org/{z}/{x}/{y}.png', {
      maxNativeZoom: 19, maxZoom: 21,
      attribution: '&copy; <a href="https://www.openstreetmap.org/copyright">OpenStreetMap</a>',
    });
    const sat = L.tileLayer('https://server.arcgisonline.com/ArcGIS/rest/services/World_Imagery/MapServer/tile/{z}/{y}/{x}', {
      maxNativeZoom: 19, maxZoom: 21,
      attribution: 'Tiles &copy; Esri &mdash; Source: Esri, Maxar, Earthstar Geographics, GIS User Community',
    });
    layers = { map: osm, satellite: sat };
    currentLayer = data.layer === 'satellite' ? 'satellite' : 'map';

    map = L.map('map', { zoomControl: true, attributionControl: true, maxZoom: 21, doubleClickZoom: false });
    map.attributionControl.setPrefix('<a href="https://leafletjs.com">Leaflet</a>');
    layers[currentLayer].addTo(map);
    L.control.layers({ [text('layerMap')]: osm, [text('layerSatellite')]: sat }, null, { position: 'topright', collapsed: false }).addTo(map);
    map.on('baselayerchange', e => { currentLayer = e.layer === sat ? 'satellite' : 'map'; });
    L.control.scale({ imperial: false, position: 'bottomright' }).addTo(map);

    if (data.center) {
      map.setView([data.center.lat, data.center.lon], data.center.zoom ?? 18);
      found = data.fromVideo || (data.center.zoom ?? 0) >= MIN_PICK_ZOOM;
    } else {
      map.setView([42.5, 12.5], 6);   // Italia
    }

    if (data.videoLocation) {
      L.circleMarker([data.videoLocation.lat, data.videoLocation.lon], { radius: 6, color: '#fff', weight: 2, fillColor: '#2fcb79', fillOpacity: 1 })
        .bindTooltip(text('videoPosition'), { direction: 'top', offset: [0, -6] })
        .addTo(map);
    }

    if (data.a && data.b) {
      setPoint('a', data.a.lat, data.a.lon);
      setPoint('b', data.b.lat, data.b.lon);
      found = true;
    }

    map.on('click', onMapClick);
    map.on('zoomend', () => {
      if (map.getZoom() >= MIN_PICK_ZOOM) found = true;
      refresh();
    });
    $('search').addEventListener('submit', e => { e.preventDefault(); search(); });
    $('use').addEventListener('click', useDistance);
    $('reset').addEventListener('click', resetPoints);
    $('cancel').addEventListener('click', () => post({ type: 'cancel', ...view() }));
    $('find-hint').textContent = data.fromVideo ? text('findFromVideo') : text('findHint');
    refresh();
  }

  function view() {
    const c = map.getCenter();
    return { layer: currentLayer, view: { lat: c.lat, lon: c.lng, zoom: map.getZoom() } };
  }

  // ───────────── Punti A e B ─────────────

  function onMapClick(e) {
    if (map.getZoom() < MIN_PICK_ZOOM) {
      // Troppo lontano per un punto preciso: prima si avvicina la mappa lì dove si è cliccato.
      map.setView(e.latlng, MIN_PICK_ZOOM);
      toast(text('zoomIn'));
      return;
    }

    if (!a) setPoint('a', e.latlng.lat, e.latlng.lng);
    else if (!b) setPoint('b', e.latlng.lat, e.latlng.lng);
    else toast(text('dragToMove'));
  }

  function pinIcon(which) {
    return L.divIcon({ className: '', html: `<div class="pin ${which}">${which.toUpperCase()}</div>`, iconSize: [26, 26], iconAnchor: [13, 13] });
  }

  function setPoint(which, lat, lon) {
    const p = { lat, lon, mpp: TsaGeo.metersPerPixel(lat, map.getZoom()) };
    let marker = which === 'a' ? markerA : markerB;
    if (!marker) {
      marker = L.marker([lat, lon], { icon: pinIcon(which), draggable: true, keyboard: false, autoPan: true }).addTo(map);
      marker.on('drag dragend', () => {
        const ll = marker.getLatLng();
        const q = { lat: ll.lat, lon: ll.lng, mpp: TsaGeo.metersPerPixel(ll.lat, map.getZoom()) };
        if (which === 'a') a = q; else b = q;
        refresh();
      });
      if (which === 'a') markerA = marker; else markerB = marker;
    } else {
      marker.setLatLng([lat, lon]);
    }

    if (which === 'a') a = p; else b = p;
    refresh();
  }

  function resetPoints() {
    for (const m of [markerA, markerB, link]) if (m) map.removeLayer(m);
    markerA = markerB = link = null;
    a = b = null;
    refresh();
  }

  function distance() {
    return a && b ? TsaGeo.distanceMeters(a, b) : null;
  }

  /** Incertezza dovuta ai clic: due punti, ciascuno con un errore di qualche pixel. */
  function precision() {
    if (!a || !b) return null;
    return Math.hypot(a.mpp, b.mpp) * CLICK_ERROR_PX;
  }

  function useDistance() {
    const d = distance();
    if (d === null || d <= 0) return;
    post({ type: 'result', a: { lat: a.lat, lon: a.lon }, b: { lat: b.lat, lon: b.lon }, meters: d, precision: precision(), ...view() });
  }

  // ───────────── Guida: un solo passo evidenziato ─────────────

  function refresh() {
    const d = distance();
    const step = !found && !a ? 'find' : !a ? 'a' : !b ? 'b' : 'result';
    const order = ['find', 'a', 'b', 'result'];
    const idx = order.indexOf(step);
    [['step-find', 'find'], ['step-a', 'a'], ['step-b', 'b'], ['step-result', 'result']].forEach(([id, s]) => {
      const i = order.indexOf(s);
      $(id).className = 'card' + (s === step ? ' on' : i < idx ? ' done' : ' later');
    });

    $('hint-a').textContent = a ? text('placed') : text('hintA');
    $('hint-b').textContent = b ? text('placed') : text('hintB');
    $('distance').textContent = d === null ? '–' : fmt(d);
    const p = precision();
    $('precision').textContent = p === null ? text('resultHint') : text('precision', fmt(p, 2));
    $('use').disabled = d === null || d <= 0;
    $('reset').hidden = !a && !b;
    $('map').classList.toggle('picking', found && (!a || !b));
    map.getContainer().classList.toggle('picking', found && (!a || !b) && map.getZoom() >= MIN_PICK_ZOOM);

    if (a && b) {
      const ll = [[a.lat, a.lon], [b.lat, b.lon]];
      if (!link) {
        link = L.polyline(ll, { color: '#fff', weight: 2, dashArray: '6 5', interactive: false }).addTo(map);
        link.bindTooltip('', { permanent: true, direction: 'center', className: 'dist' });
      }
      link.setLatLngs(ll);
      link.setTooltipContent(fmt(d) + ' m');
    } else if (link) {
      map.removeLayer(link);
      link = null;
    }
  }

  // ───────────── Ricerca del luogo (Nominatim di OpenStreetMap) ─────────────

  async function search() {
    const q = $('search-text').value.trim();
    const list = $('search-results');
    list.innerHTML = '';
    $('search-error').textContent = '';
    if (!q) return;
    $('search-button').disabled = true;
    try {
      const url = 'https://nominatim.openstreetmap.org/search?format=jsonv2&limit=5&q=' + encodeURIComponent(q) +
        '&accept-language=' + encodeURIComponent(language);
      const res = await fetch(url, { headers: { Accept: 'application/json' } });
      if (!res.ok) throw new Error('HTTP ' + res.status);
      const items = await res.json();
      if (!items.length) { $('search-error').textContent = text('notFound'); return; }
      for (const it of items) {
        const li = document.createElement('li');
        li.textContent = it.display_name;
        li.addEventListener('click', () => goTo(it));
        list.appendChild(li);
      }
      if (items.length === 1) goTo(items[0]);
    } catch (e) {
      $('search-error').textContent = text('searchError');
    } finally {
      $('search-button').disabled = false;
    }
  }

  function goTo(item) {
    $('search-results').innerHTML = '';
    const lat = +item.lat, lon = +item.lon;
    if (item.boundingbox) {
      const [s, n, w, e] = item.boundingbox.map(Number);
      map.fitBounds([[s, w], [n, e]], { maxZoom: MIN_PICK_ZOOM });
    } else {
      map.setView([lat, lon], MIN_PICK_ZOOM);
    }
  }

  let toastTimer = 0;
  function toast(msg) {
    const t = $('zoom-hint');
    t.textContent = msg;
    t.classList.add('show');
    clearTimeout(toastTimer);
    toastTimer = setTimeout(() => t.classList.remove('show'), 2600);
  }

  // Dati dal programma; nella prova senza WebView2 si chiama window.tsaMap.init(...).
  if (host) host.addEventListener('message', e => { if (e.data && e.data.type === 'init') init(e.data); });
  window.tsaMap = {
    init,
    state: () => ({ a, b, distance: distance(), precision: precision(), zoom: map && map.getZoom(), cards: ['step-find', 'step-a', 'step-b', 'step-result'].map(id => $(id).className) }),
    click: (lat, lon) => onMapClick({ latlng: L.latLng(lat, lon) }),
    setView: (lat, lon, zoom) => map.setView([lat, lon], zoom, { animate: false }),
  };
  post({ type: 'ready' });
})();
