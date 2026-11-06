// Rilevatore YOLOX nel browser con ONNX Runtime Web: scheda grafica (WebGPU) se dà risultati
// corretti, altrimenti CPU (WebAssembly). Pre e post-elaborazione identiche a
// src/TrafficSpeedAnalyzer.Detection/YoloxDetector.cs: zona letterbox allineata in alto a
// sinistra con grigio 114, ordine BGR, valori 0–255, griglia con passi 8/16/32, NMS
// indipendente dalla classe.

// Indici COCO → classi del nucleo C# (VehicleClass: Car=0, Motorcycle=1, Bus=2, Truck=3, Bicycle=4).
export const COCO = { bicycle: 1, car: 2, motorcycle: 3, bus: 5, truck: 7 };
const CLASS_MAP = new Map([[2, 0], [3, 1], [5, 2], [7, 3], [1, 4]]);
export const CLASS_NAMES = ['Auto', 'Moto', 'Autobus', 'Camion', 'Bicicletta', 'Altro'];
export const PAD_VALUE = 114;

/** Versione degli script: mostrata nella pagina per capire quale codice sta girando. */
export const BUILD = '2026-10-30 · 6 (misura subito dopo la linea)';

/** Modelli noti (stessi file e impronte SHA-256 di ModelCatalog.cs). */
export const MODELS = [
  { id: 'yolox_s', name: 'Preciso (YOLOX-S)', file: 'yolox_s.onnx', size: 640, bytes: 35858002,
    sha256: 'c5c2d13e59ae883e6af3b45daea64af4833a4951c92d116ec270d9ddbe998063' },
  { id: 'yolox_tiny', name: 'Bilanciato (YOLOX-Tiny)', file: 'yolox_tiny.onnx', size: 416, bytes: 20219662,
    sha256: '427cc366d34e27ff7a03e2899b5e3671425c262ea2291f88bb942bc1cc70b0f7' },
  { id: 'yolox_nano', name: 'Veloce (YOLOX-Nano)', file: 'yolox_nano.onnx', size: 416, bytes: 3659407,
    sha256: 'c789161ed43c8269fcd4e67c67eeeb4e80c622da2eb296a20bc6007bd18a0b7d' },
];

export async function sha256Hex(bytes) {
  const h = await crypto.subtle.digest('SHA-256', bytes);
  return Array.from(new Uint8Array(h), b => b.toString(16).padStart(2, '0')).join('');
}

export function buildGrid(size) {
  const g = [];
  for (const s of [8, 16, 32]) {
    const n = size / s;
    for (let y = 0; y < n; y++) for (let x = 0; x < n; x++) g.push(x, y, s);
  }
  return new Float32Array(g);
}

/** Zona in pixel interi interna all'immagine (come Roi.From in C#). */
export function roiFrom(region, width, height) {
  if (!region) return { x: 0, y: 0, w: width, h: height };
  const clamp = (v, a, b) => Math.min(Math.max(v, a), b);
  const x1 = clamp(Math.floor(region.x), 0, width - 1);
  const y1 = clamp(Math.floor(region.y), 0, height - 1);
  const x2 = clamp(Math.ceil(region.x + region.w), x1 + 1, width);
  const y2 = clamp(Math.ceil(region.y + region.h), y1 + 1, height);
  return { x: x1, y: y1, w: x2 - x1, h: y2 - y1 };
}

export function letterboxScale(roi, size) {
  const scale = Math.min(size / roi.w, size / roi.h);
  return { scale, outW: Math.min(size, Math.round(roi.w * scale)), outH: Math.min(size, Math.round(roi.h * scale)) };
}

/** Converte i pixel RGBA della zona letterbox nel tensore NCHW in ordine BGR. */
export function rgbaToTensor(rgba, size, tensor) {
  const plane = size * size;
  for (let i = 0, p = 0; i < plane; i++, p += 4) {
    tensor[i] = rgba[p + 2];             // B
    tensor[plane + i] = rgba[p + 1];     // G
    tensor[2 * plane + i] = rgba[p];     // R
  }
  return tensor;
}

/** Disegna la zona del fotogramma nel canvas del modello e ne ricava il tensore. */
export function prepareInput(sourceCanvas, roi, size, ctx, tensor) {
  const { scale, outW, outH } = letterboxScale(roi, size);
  ctx.fillStyle = `rgb(${PAD_VALUE},${PAD_VALUE},${PAD_VALUE})`;
  ctx.fillRect(0, 0, size, size);
  ctx.drawImage(sourceCanvas, roi.x, roi.y, roi.w, roi.h, 0, 0, outW, outH);
  rgbaToTensor(ctx.getImageData(0, 0, size, size).data, size, tensor);
  return scale;
}

export function iou(a, b) {
  const x1 = Math.max(a.x, b.x), y1 = Math.max(a.y, b.y);
  const x2 = Math.min(a.x + a.w, b.x + b.w), y2 = Math.min(a.y + a.h, b.y + b.h);
  const inter = Math.max(0, x2 - x1) * Math.max(0, y2 - y1);
  const union = Math.max(0, a.w) * Math.max(0, a.h) + Math.max(0, b.w) * Math.max(0, b.h) - inter;
  return union > 0 ? inter / union : 0;
}

export function nms(dets, threshold) {
  dets.sort((a, b) => b.score - a.score);
  const keep = [];
  for (const d of dets) if (!keep.some(k => iou(d, k) > threshold)) keep.push(d);
  return keep;
}

/**
 * Uscita del modello [1, N, 5 + classi] → rilevamenti in pixel del fotogramma, limitati alla zona.
 * Ogni rilevamento: { x, y, w, h, cls (VehicleClass), score }.
 */
export function decodeOutput(output, grid, numClasses, roi, scale, opts = {}) {
  const minScore = opts.minScore ?? 0.1;
  const classes = opts.includeBicycles ? [2, 3, 5, 7, 1] : [2, 3, 5, 7];
  const stride = 5 + numClasses;
  const anchors = Math.min(grid.length / 3, Math.floor(output.length / stride));
  const clamp = (v, a, b) => Math.min(Math.max(v, a), b);
  const out = [];
  for (let k = 0; k < anchors; k++) {
    const o = k * stride;
    const obj = output[o + 4];
    if (obj < minScore) continue;
    let best = 0, bestClass = -1;
    for (const c of classes) {
      const s = obj * output[o + 5 + c];
      if (s > best) { best = s; bestClass = c; }
    }
    if (bestClass < 0 || best < minScore) continue;
    const gx = grid[3 * k], gy = grid[3 * k + 1], st = grid[3 * k + 2];
    const cx = (output[o] + gx) * st, cy = (output[o + 1] + gy) * st;
    const w = Math.exp(output[o + 2]) * st, h = Math.exp(output[o + 3]) * st;
    const x1 = clamp((cx - w / 2) / scale + roi.x, roi.x, roi.x + roi.w);
    const y1 = clamp((cy - h / 2) / scale + roi.y, roi.y, roi.y + roi.h);
    const x2 = clamp((cx + w / 2) / scale + roi.x, roi.x, roi.x + roi.w);
    const y2 = clamp((cy + h / 2) / scale + roi.y, roi.y, roi.y + roi.h);
    if (x2 - x1 < 2 || y2 - y1 < 2) continue;
    out.push({ x: x1, y: y1, w: x2 - x1, h: y2 - y1, cls: CLASS_MAP.get(bestClass) ?? 5, score: best });
  }
  return nms(out, opts.nmsIoU ?? 0.5);
}

/** Immagine di prova deterministica per confrontare GPU e CPU. */
export function validationInput(size) {
  const t = new Float32Array(3 * size * size);
  const plane = size * size;
  let seed = 1234;
  const rnd = () => { seed = (seed * 1103515245 + 12345) >>> 0; return (seed >>> 16) % 20; };
  for (let c = 0; c < 3; c++) for (let y = 0; y < size; y++) for (let x = 0; x < size; x++) {
    t[c * plane + y * size + x] = ((x * (c + 1) + y * 2) % 256 + ((((x / 32) | 0) + ((y / 32) | 0)) % 2) * 60 + rnd()) % 256;
  }
  return t;
}

/** Stesso criterio di YoloxDetector.OutputsMatch. */
export function outputsMatch(expected, actual, stride = 85) {
  if (!actual || expected.length !== actual.length || expected.length === 0) return false;
  let sumAbs = 0, maxScoreDiff = 0;
  for (let i = 0; i < expected.length; i++) {
    const a = actual[i];
    if (!Number.isFinite(a)) return false;
    const d = Math.abs(expected[i] - a);
    sumAbs += d;
    if (i % stride >= 4 && d > maxScoreDiff) maxScoreDiff = d;
  }
  return sumAbs / expected.length < 0.05 && maxScoreDiff < 0.1;
}

let ortModule = null;
async function loadOrt() {
  if (ortModule) return ortModule;
  const base = new URL('../lib/ort/', import.meta.url).href;
  try {
    ortModule = await import(base + 'ort.webgpu.min.mjs');
  } catch (e) {
    throw new Error('ONNX Runtime Web non trovato in wwwroot/lib/ort: esegui scripts\\web-deps.ps1. (' + e.message + ')');
  }
  ortModule.env.wasm.wasmPaths = base;
  // Più thread solo se la pagina è isolata (intestazioni COOP/COEP), altrimenti uno.
  ortModule.env.wasm.numThreads = globalThis.crossOriginIsolated ? Math.min(8, navigator.hardwareConcurrency || 4) : 1;
  return ortModule;
}

/**
 * Crea il rilevatore. Prova WebGPU e lo accetta solo se sull'immagine di prova dà gli stessi
 * risultati della CPU, come fa l'app desktop con DirectML.
 */
export async function createDetector(modelBytes, { size, preferGpu = true, log = () => {}, ort: injected } = {}) {
  const ort = injected ?? await loadOrt();
  const t0 = performance.now();
  const cpu = await ort.InferenceSession.create(modelBytes, { executionProviders: ['wasm'], graphOptimizationLevel: 'all' });
  const inputName = cpu.inputNames[0], outputName = cpu.outputNames[0];
  log(`Sessione CPU pronta in ${(performance.now() - t0).toFixed(0)} ms (${ort.env.wasm.numThreads} thread).`);

  const runOn = async (session, data) => {
    const r = await session.run({ [inputName]: new ort.Tensor('float32', data, [1, 3, size, size]) });
    const out = r[outputName];
    const values = out.data instanceof Float32Array ? out.data : await out.getData();
    return { values, dims: out.dims };
  };

  const probe = validationInput(size);
  const expected = await runOn(cpu, probe);
  const numClasses = expected.dims[expected.dims.length - 1] - 5;
  if (numClasses < 8) throw new Error('Formato di uscita del modello non riconosciuto.');

  // Nota: nessuna sessione viene rilasciata finché il rilevatore è in uso. In ONNX Runtime Web le
  // sessioni CPU e WebGPU condividono lo stesso motore e rilasciarne una può invalidare i dati
  // della scheda grafica dell'altra ("no GPU data for input"). La sessione CPU resta pronta
  // anche come riserva se WebGPU sbaglia durante l'analisi.
  let session = cpu, gpu = null, provider = 'CPU (WebAssembly)', note = '';
  if (preferGpu && typeof navigator !== 'undefined' && navigator.gpu) {
    try {
      gpu = await ort.InferenceSession.create(modelBytes, { executionProviders: ['webgpu'], graphOptimizationLevel: 'all' });
      // Due esecuzioni: alcuni errori compaiono solo dalla seconda in poi.
      const first = await runOn(gpu, probe);
      const second = await runOn(gpu, probe);
      if (outputsMatch(expected.values, first.values, 5 + numClasses) && outputsMatch(expected.values, second.values, 5 + numClasses)) {
        session = gpu; provider = 'GPU (WebGPU)';
      } else {
        note = 'la scheda grafica dava risultati non validi';
      }
    } catch (e) {
      note = 'WebGPU non utilizzabile: ' + (e.message || e);
    }
  } else if (preferGpu) {
    note = 'WebGPU non disponibile in questo browser';
  }
  log(`Esecutore: ${provider}${note ? ' (' + note + ')' : ''}.`);

  const grid = buildGrid(size);
  const tensor = new Float32Array(3 * size * size);
  const canvas = new OffscreenCanvas(size, size);
  const ctx = canvas.getContext('2d', { willReadFrequently: true });

  const detector = {
    provider, note, size, numClasses,
    threads: ort.env.wasm.numThreads,
    /** Numero di volte in cui WebGPU ha fallito durante l'analisi (poi si resta su CPU). */
    gpuFailures: 0,
    /** Rileva i veicoli nella zona del canvas (coordinate del video visualizzato). */
    async detect(sourceCanvas, region, opts) {
      const roi = roiFrom(region, sourceCanvas.width, sourceCanvas.height);
      const scale = prepareInput(sourceCanvas, roi, size, ctx, tensor);
      let values;
      try {
        ({ values } = await runOn(session, tensor));
      } catch (e) {
        if (session === cpu) throw e;
        // WebGPU in errore: stesso fotogramma su CPU, e da qui in poi solo CPU.
        detector.gpuFailures++;
        session = cpu;
        detector.provider = 'CPU (WebAssembly)';
        detector.note = 'WebGPU in errore durante l\'analisi, passato alla CPU: ' + (e.message || e);
        log(detector.note);
        ({ values } = await runOn(cpu, tensor));
      }
      return decodeOutput(values, grid, numClasses, roi, scale, opts);
    },
    async release() {
      await gpu?.release?.();
      await cpu.release?.();
    },
  };
  return detector;
}

/**
 * Rileva in modo economico se nella zona è cambiato qualcosa (come MotionGate.cs): campiona
 * la zona a 1/4 della risoluzione e conta i punti con luminosità cambiata oltre la soglia.
 */
export function createMotionGate({ changedFraction = 0.003, pixelThreshold = 18, sampleStep = 4 } = {}) {
  let previous = null, prevKey = '';
  const canvas = new OffscreenCanvas(1, 1);
  const ctx = canvas.getContext('2d', { willReadFrequently: true });
  return {
    lastChangedFraction: 1,
    hasMotion(sourceCanvas, region) {
      const roi = roiFrom(region, sourceCanvas.width, sourceCanvas.height);
      const cols = Math.max(1, Math.floor(roi.w / sampleStep)), rows = Math.max(1, Math.floor(roi.h / sampleStep));
      if (canvas.width !== cols || canvas.height !== rows) { canvas.width = cols; canvas.height = rows; }
      ctx.drawImage(sourceCanvas, roi.x, roi.y, cols * sampleStep, rows * sampleStep, 0, 0, cols, rows);
      const px = ctx.getImageData(0, 0, cols, rows).data;
      const gray = new Uint8Array(cols * rows);
      for (let i = 0, p = 0; i < gray.length; i++, p += 4) gray[i] = (px[p + 2] * 29 + px[p + 1] * 150 + px[p] * 77) >> 8;
      const key = `${roi.x},${roi.y},${roi.w},${roi.h}`;
      const prev = previous;
      const same = prev && prev.length === gray.length && prevKey === key;
      previous = gray; prevKey = key;
      if (!same) { this.lastChangedFraction = 1; return true; }
      let changed = 0;
      for (let i = 0; i < gray.length; i++) if (Math.abs(gray[i] - prev[i]) > pixelThreshold) changed++;
      this.lastChangedFraction = changed / gray.length;
      return this.lastChangedFraction >= changedFraction;
    },
    reset() { previous = null; },
  };
}
