// Collegamento tra l'interfaccia Blazor (C#) e le parti che lavorano sui fotogrammi.
// Il file video resta sul disco dell'utente: viene letto a pezzi, mai caricato in rete.
import { openMp4, blobSource } from './mp4demux.js';
import { checkSupport, frames, frameAt, drawFrame } from './decoder.js';
import { createDetector, createMotionGate, MODELS, sha256Hex, CLASS_NAMES, BUILD } from './yolox.js';

let dotnet = null;
let els = null;
let video = null;          // indice del file aperto
let support = null;        // configurazione del decodificatore
let detector = null;
let frameCanvas = null;    // fotogramma corrente a piena risoluzione
let frameCtx = null;
let lines = { a: null, b: null, region: null };
let tracked = [];
let drawMode = null;
let dragging = null;
let abort = null;
let running = false;
let currentFrame = 0;
let lastModel = null;      // byte e nome dell'ultimo modello, per ricaricarlo con un altro esecutore
let preferGpu = true;

const COLORS = { a: '#4cc2ff', b: '#ffb900', region: 'rgba(255,255,255,0.55)', confirmed: '#6ccb5f', tentative: '#c0c0c0' };

export function init(dotnetRef, canvas, videoInput, modelInput) {
  dotnet = dotnetRef;
  els = { canvas, videoInput, modelInput };
  frameCanvas = new OffscreenCanvas(16, 9);
  frameCtx = frameCanvas.getContext('2d');
  videoInput.addEventListener('change', () => {
    const f = videoInput.files?.[0];
    if (f) openFile(f);
    videoInput.value = '';
  });
  canvas.addEventListener('pointerdown', onPointerDown);
  canvas.addEventListener('pointermove', onPointerMove);
  canvas.addEventListener('pointerup', onPointerUp);
  canvas.addEventListener('pointercancel', () => { dragging = null; render(); });
  return engineInfo();
}

export function engineInfo() {
  return {
    webCodecs: typeof VideoDecoder !== 'undefined',
    webGpu: !!navigator.gpu,
    crossOriginIsolated: !!globalThis.crossOriginIsolated,
    cores: navigator.hardwareConcurrency || 0,
    build: BUILD,
    userAgent: navigator.userAgent,
  };
}

// ---------------------------------------------------------------- modello

async function initDetector(bytes, name) {
  lastModel = { bytes, name };
  const t0 = performance.now();
  const hash = await sha256Hex(bytes);
  const known = MODELS.find(m => m.sha256 === hash);
  const size = known?.size ?? (/nano|tiny/i.test(name) ? 416 : 640);
  const log = [];
  await detector?.release();
  detector = null;
  detector = await createDetector(bytes, { size, preferGpu, log: m => log.push(m) });
  return {
    ok: true, name: known?.name ?? name, verified: !!known, provider: detector.provider, note: detector.note,
    size, threads: detector.threads, loadMs: performance.now() - t0, log: log.join(' '), build: BUILD,
  };
}

/** Ricarica l'ultimo modello scegliendo l'esecutore (true = prova WebGPU, false = solo CPU). */
export async function setPreferGpu(value) {
  preferGpu = !!value;
  if (!lastModel || running) return null;
  try {
    return await initDetector(lastModel.bytes, lastModel.name);
  } catch (e) {
    return { ok: false, error: e.message || String(e), build: BUILD };
  }
}

export async function loadModelFromUrl(url) {
  try {
    const res = await fetch(url);
    if (!res.ok) return { ok: false, missing: true, error: `Modello non presente sul sito (${url}).` };
    return await initDetector(new Uint8Array(await res.arrayBuffer()), url.split('/').pop());
  } catch (e) {
    return { ok: false, error: e.message || String(e) };
  }
}

export async function loadModelFromInput() {
  const f = els.modelInput.files?.[0];
  if (!f) return { ok: false, error: 'Nessun file scelto.' };
  try {
    return await initDetector(new Uint8Array(await f.arrayBuffer()), f.name);
  } catch (e) {
    return { ok: false, error: e.message || String(e) };
  } finally {
    els.modelInput.value = '';
  }
}

// ---------------------------------------------------------------- video

function videoInfo(v, sup) {
  return {
    name: v.name, fileSize: v.fileSize, codec: v.codec, codedWidth: v.codedWidth, codedHeight: v.codedHeight,
    rotation: v.rotation, displayWidth: v.displayWidth, displayHeight: v.displayHeight, frameCount: v.frameCount,
    duration: v.duration, meanFps: v.meanFps, minFrameInterval: v.minFrameInterval, maxFrameInterval: v.maxFrameInterval,
    firstTime: v.frameTimes[0] ?? 0, creationDate: v.creationDate, creationSource: v.creationSource,
    supported: sup.supported, hardware: !!sup.hardware, reason: sup.reason ?? null,
  };
}

async function openFile(file) {
  cancel();
  let info;
  try {
    const v = await openMp4(blobSource(file));
    const sup = await checkSupport(v);
    video = v; support = sup; tracked = []; lines = { a: null, b: null, region: null };
    info = videoInfo(v, sup);
    if (sup.supported) await showFrame(0);
  } catch (e) {
    video = null; support = null;
    info = { error: e.message || String(e), name: file.name };
  }
  await dotnet.invokeMethodAsync('OnVideoOpened', info);
}

export async function openSample(url) {
  const res = await fetch(url);
  if (!res.ok) throw new Error(`Video di prova non trovato (${url}).`);
  const blob = await res.blob();
  await openFile(new File([blob], url.split('/').pop(), { type: 'video/mp4' }));
}

export async function showFrame(index) {
  if (!video || !support?.supported || running) return null;
  index = Math.max(0, Math.min(video.frameCount - 1, index | 0));
  const { frame } = await frameAt(video, support.config, index);
  drawFrame(frameCtx, frame, video);
  frame.close();
  currentFrame = index;
  tracked = [];
  render();
  return { index, time: video.frameTimes[index] - video.frameTimes[0] };
}

/** pts del file confrontati con quelli di FFprobe (file JSON prodotto da ffprobe -show_packets). */
export async function ptsCheck(url) {
  const expected = await (await fetch(url)).json();
  const sorted = expected.packets.map(p => p.pts).sort((a, b) => a - b);
  let maxErr = 0;
  const n = Math.min(sorted.length, video?.frameCount ?? 0);
  for (let i = 0; i < n; i++) maxErr = Math.max(maxErr, Math.abs(video.frameTimes[i] - sorted[i]));
  return { expected: sorted.length, actual: video?.frameCount ?? 0, maxError: maxErr };
}

/** Elenco dei pts (uno per riga, ordine di presentazione) da confrontare con FFprobe. */
export function ptsText() {
  if (!video) return '';
  return Array.from(video.frameTimes, t => t.toFixed(6)).join('\r\n') + '\r\n';
}

// ---------------------------------------------------------------- linee

export function setLines(a, b, region) {
  lines = { a, b, region };
  render();
}

export function setDrawMode(mode) {
  drawMode = mode;
  els.canvas.style.cursor = mode ? 'crosshair' : 'default';
}

function toVideo(e) {
  const r = els.canvas.getBoundingClientRect();
  return { x: (e.clientX - r.left) * els.canvas.width / r.width, y: (e.clientY - r.top) * els.canvas.height / r.height };
}

function handleRadius() {
  const r = els.canvas.getBoundingClientRect();
  return 12 * els.canvas.width / Math.max(1, r.width);
}

function onPointerDown(e) {
  if (!video || running) return;
  const p = toVideo(e);
  els.canvas.setPointerCapture(e.pointerId);
  if (drawMode) {
    dragging = { which: drawMode, line: [p.x, p.y, p.x, p.y], end: 1 };
    return;
  }
  // Trascinamento di un estremo di una linea esistente.
  const rad = handleRadius();
  for (const which of ['a', 'b']) {
    const l = lines[which];
    if (!l) continue;
    for (const end of [0, 1]) {
      if (Math.hypot(l[end * 2] - p.x, l[end * 2 + 1] - p.y) <= rad) {
        dragging = { which: which.toUpperCase(), line: l.slice(), end };
        return;
      }
    }
  }
}

function onPointerMove(e) {
  if (!dragging) return;
  const p = toVideo(e);
  const cl = (v, max) => Math.max(0, Math.min(max, v));
  dragging.line[dragging.end * 2] = cl(p.x, els.canvas.width);
  dragging.line[dragging.end * 2 + 1] = cl(p.y, els.canvas.height);
  render();
}

async function onPointerUp() {
  if (!dragging) return;
  const d = dragging;
  dragging = null;
  const [x1, y1, x2, y2] = d.line;
  if (Math.hypot(x2 - x1, y2 - y1) < 3) { render(); return; }
  if (d.which === 'A') lines.a = d.line; else lines.b = d.line;
  render();
  await dotnet.invokeMethodAsync('OnLineDrawn', d.which, x1, y1, x2, y2);
}

// ---------------------------------------------------------------- disegno

function render() {
  const c = els.canvas;
  if (!video) { c.width = 16; c.height = 9; return; }
  if (c.width !== video.displayWidth || c.height !== video.displayHeight) { c.width = video.displayWidth; c.height = video.displayHeight; }
  const ctx = c.getContext('2d');
  ctx.drawImage(frameCanvas, 0, 0);
  const unit = Math.max(1, c.width / 900);

  if (lines.region) {
    const [x, y, w, h] = lines.region;
    ctx.save();
    ctx.setLineDash([8 * unit, 6 * unit]);
    ctx.strokeStyle = COLORS.region; ctx.lineWidth = 1.5 * unit;
    ctx.strokeRect(x, y, w, h);
    ctx.restore();
  }

  const drawLine = (l, color, label) => {
    if (!l) return;
    ctx.save();
    ctx.strokeStyle = color; ctx.fillStyle = color; ctx.lineWidth = 3 * unit;
    ctx.beginPath(); ctx.moveTo(l[0], l[1]); ctx.lineTo(l[2], l[3]); ctx.stroke();
    for (const k of [0, 2]) { ctx.beginPath(); ctx.arc(l[k], l[k + 1], 5 * unit, 0, Math.PI * 2); ctx.fill(); }
    ctx.font = `600 ${18 * unit}px system-ui, sans-serif`;
    ctx.lineWidth = 4 * unit; ctx.strokeStyle = 'rgba(0,0,0,0.7)';
    const lx = l[0] + 8 * unit, ly = l[1] - 8 * unit;
    ctx.strokeText(label, lx, ly); ctx.fillText(label, lx, ly);
    ctx.restore();
  };
  const live = dragging ? { [dragging.which.toLowerCase()]: dragging.line } : {};
  drawLine(live.a ?? lines.a, COLORS.a, 'A');
  drawLine(live.b ?? lines.b, COLORS.b, 'B');

  for (const t of tracked) {
    ctx.save();
    ctx.strokeStyle = t.confirmed ? COLORS.confirmed : COLORS.tentative;
    ctx.lineWidth = 2 * unit;
    ctx.strokeRect(t.x, t.y, t.w, t.h);
    ctx.beginPath(); ctx.arc(t.x + t.w / 2, t.y + t.h, 4 * unit, 0, Math.PI * 2); ctx.fillStyle = ctx.strokeStyle; ctx.fill();
    ctx.font = `600 ${14 * unit}px system-ui, sans-serif`;
    const label = t.kmh >= 0 ? `${t.id} · ${t.kmh.toFixed(1).replace('.', ',')} km/h` : `${t.id}`;
    if (t.kmh >= 0) { ctx.font = `700 ${16 * unit}px system-ui, sans-serif`; ctx.fillStyle = '#ffffff'; }
    ctx.lineWidth = 3 * unit; ctx.strokeStyle = 'rgba(0,0,0,0.75)';
    ctx.strokeText(label, t.x + 3 * unit, t.y - 4 * unit);
    ctx.fillText(label, t.x + 3 * unit, t.y - 4 * unit);
    ctx.restore();
  }
}

// ---------------------------------------------------------------- analisi

export function analyze(p) {
  if (running || !video || !detector) return false;
  running = true;
  abort = new AbortController();
  // Lo stato torna libero prima di avvisare C#, che così può avviare subito un'altra analisi.
  run(p, abort.signal)
    .catch(e => ({ error: e.message || String(e) }))
    .then(summary => { running = false; abort = null; return dotnet.invokeMethodAsync('OnAnalysisFinished', summary); });
  return true;
}

export function cancel() {
  abort?.abort();
}

async function run(p, signal) {
  const region = { x: p.region[0], y: p.region[1], w: p.region[2], h: p.region[3] };
  const work = new OffscreenCanvas(video.displayWidth, video.displayHeight);
  const wctx = work.getContext('2d');
  const gate = createMotionGate();
  const first = video.frameTimes[0];
  const total = p.end - p.start + 1;
  let done = 0, detected = 0, skipped = 0, sinceDetect = 0, active = 0, detectMs = 0;
  let lastPreview = 0, lastProgress = 0, lastIndex = p.start;
  const t0 = performance.now();

  for await (const { frame, index, time } of frames(video, support.config, p.start, p.end, signal)) {
    drawFrame(wctx, frame, video);
    frame.close();
    const motion = gate.hasMotion(work, region);
    const runModel = !p.skipStatic || motion || active > 0 || sinceDetect >= p.maxSkipped;
    if (runModel) {
      const t1 = performance.now();
      const dets = await detector.detect(work, region, { minScore: p.minScore, includeBicycles: p.includeBicycles });
      detectMs += performance.now() - t1;
      detected++; sinceDetect = 0;
      const packed = new Array(dets.length * 6);
      dets.forEach((d, i) => { packed[i * 6] = d.x; packed[i * 6 + 1] = d.y; packed[i * 6 + 2] = d.w; packed[i * 6 + 3] = d.h; packed[i * 6 + 4] = d.cls; packed[i * 6 + 5] = d.score; });
      const r = await dotnet.invokeMethodAsync('OnFrameDetections', index, time - first, packed);
      active = r[0];
      tracked = [];
      for (let k = 0; k < r[1]; k++) {
        const i = 2 + k * 7;
        tracked.push({ id: r[i], x: r[i + 1], y: r[i + 2], w: r[i + 3], h: r[i + 4], confirmed: r[i + 5] > 0, kmh: r[i + 6] });
      }
    } else {
      skipped++; sinceDetect++;
      tracked = [];
    }
    done++; lastIndex = index;
    const now = performance.now();
    if (now - lastPreview > 120) {
      lastPreview = now;
      frameCtx.canvas.width = work.width; frameCtx.canvas.height = work.height;
      frameCtx.drawImage(work, 0, 0);
      currentFrame = index;
      render();
    }
    if (now - lastProgress > 300) {
      lastProgress = now;
      await dotnet.invokeMethodAsync('OnProgress', {
        done, total, frameIndex: index, detected, skipped, framesPerSecond: done / ((now - t0) / 1000),
        meanDetectMs: detected ? detectMs / detected : 0,
      });
    }
  }
  frameCtx.canvas.width = work.width; frameCtx.canvas.height = work.height;
  frameCtx.drawImage(work, 0, 0);
  currentFrame = lastIndex;
  tracked = [];
  render();
  const elapsed = performance.now() - t0;
  return {
    done, total, detected, skipped, cancelled: signal.aborted, elapsedMs: elapsed, lastFrame: lastIndex,
    framesPerSecond: done / Math.max(1e-3, elapsed / 1000), meanDetectMs: detected ? detectMs / detected : 0,
    provider: detector.provider,
    providerNote: detector.gpuFailures ? detector.note : null,
  };
}

// ---------------------------------------------------------------- file

export function download(name, bytes, mime) {
  const blob = new Blob([bytes], { type: mime || 'application/octet-stream' });
  const a = document.createElement('a');
  a.href = URL.createObjectURL(blob);
  a.download = name;
  document.body.appendChild(a);
  a.click();
  a.remove();
  setTimeout(() => URL.revokeObjectURL(a.href), 10000);
}

export function downloadText(name, text, mime) {
  download(name, new TextEncoder().encode(text), mime || 'text/plain;charset=utf-8');
}

export async function readTextFile(input) {
  const f = input.files?.[0];
  if (!f) return null;
  const text = await f.text();
  input.value = '';
  return text;
}

export { CLASS_NAMES };

/** Solo per i test della pagina: sostituisce il rilevatore. */
export function setDetectorForTests(d) { detector = d; }
