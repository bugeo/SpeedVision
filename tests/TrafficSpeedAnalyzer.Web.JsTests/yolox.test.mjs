// Test della post-elaborazione YOLOX in JavaScript (stesse regole di YoloxDetector.cs).
// Non eseguono il modello: verificano la conversione delle uscite in riquadri, la soppressione
// dei duplicati e il controllo GPU/CPU su uscite costruite a mano.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import {
  buildGrid, decodeOutput, nms, iou, roiFrom, letterboxScale, rgbaToTensor, outputsMatch, validationInput,
} from '../../src/TrafficSpeedAnalyzer.Web/wwwroot/js/yolox.js';

const SIZE = 640, NC = 80, STRIDE = 85;
const grid = buildGrid(SIZE);
const anchors = grid.length / 3;

/** Indice dell'ancora con passo s nella cella (gx, gy). */
function anchorIndex(s, gx, gy) {
  let base = 0;
  for (const st of [8, 16, 32]) {
    const n = SIZE / st;
    if (st === s) return base + gy * n + gx;
    base += n * n;
  }
  throw new Error('passo');
}

/** Scrive nell'uscita un riquadro con centro (cx, cy) e lato (w, h) in pixel del modello. */
function put(out, k, { cx, cy, w, h, obj, cls, p }) {
  const s = grid[3 * k + 2];
  const o = k * STRIDE;
  out[o] = cx / s - grid[3 * k];
  out[o + 1] = cy / s - grid[3 * k + 1];
  out[o + 2] = Math.log(w / s);
  out[o + 3] = Math.log(h / s);
  out[o + 4] = obj;
  out[o + 5 + cls] = p;
}

test('griglia: 8400 ancore per 640 px', () => {
  assert.equal(anchors, 80 * 80 + 40 * 40 + 20 * 20);
});

test('riquadro decodificato nelle coordinate del fotogramma, con zona e scala', () => {
  const out = new Float32Array(anchors * STRIDE);
  const k = anchorIndex(16, 10, 12);
  put(out, k, { cx: 168, cy: 200, w: 80, h: 40, obj: 0.9, cls: 2, p: 0.95 });
  const roi = { x: 100, y: 50, w: 1280, h: 720 };
  const { scale } = letterboxScale(roi, SIZE); // 0,5
  const [d] = decodeOutput(out, grid, NC, roi, scale);
  assert.equal(scale, 0.5);
  assert.equal(d.cls, 0); // Auto
  assert.ok(Math.abs(d.score - 0.855) < 1e-6);
  assert.ok(Math.abs(d.x - (100 + (168 - 40) * 2)) < 1e-3);
  assert.ok(Math.abs(d.y - (50 + (200 - 20) * 2)) < 1e-3);
  assert.ok(Math.abs(d.w - 160) < 1e-3);
  assert.ok(Math.abs(d.h - 80) < 1e-3);
});

test('classi: camion, autobus, moto; bici e persone escluse se non richieste', () => {
  const out = new Float32Array(anchors * STRIDE);
  put(out, anchorIndex(32, 2, 2), { cx: 80, cy: 80, w: 60, h: 60, obj: 0.9, cls: 7, p: 0.9 });   // camion
  put(out, anchorIndex(32, 8, 2), { cx: 272, cy: 80, w: 60, h: 60, obj: 0.9, cls: 5, p: 0.9 });  // autobus
  put(out, anchorIndex(32, 14, 2), { cx: 464, cy: 80, w: 60, h: 60, obj: 0.9, cls: 3, p: 0.9 }); // moto
  put(out, anchorIndex(32, 2, 10), { cx: 80, cy: 336, w: 60, h: 60, obj: 0.9, cls: 1, p: 0.9 }); // bici
  put(out, anchorIndex(32, 8, 10), { cx: 272, cy: 336, w: 60, h: 60, obj: 0.9, cls: 0, p: 0.9 }); // persona
  const roi = { x: 0, y: 0, w: SIZE, h: SIZE };
  const cls = decodeOutput(out, grid, NC, roi, 1).map(d => d.cls).sort();
  assert.deepEqual(cls, [1, 2, 3]);
  const withBikes = decodeOutput(out, grid, NC, roi, 1, { includeBicycles: true }).map(d => d.cls).sort();
  assert.deepEqual(withBikes, [1, 2, 3, 4]);
});

test('punteggio sotto la soglia scartato', () => {
  const out = new Float32Array(anchors * STRIDE);
  put(out, anchorIndex(8, 5, 5), { cx: 44, cy: 44, w: 30, h: 30, obj: 0.3, cls: 2, p: 0.3 }); // 0,09
  assert.equal(decodeOutput(out, grid, NC, { x: 0, y: 0, w: SIZE, h: SIZE }, 1).length, 0);
});

test('duplicati sovrapposti soppressi, veicoli vicini mantenuti', () => {
  const out = new Float32Array(anchors * STRIDE);
  put(out, anchorIndex(16, 10, 10), { cx: 168, cy: 168, w: 100, h: 60, obj: 0.9, cls: 2, p: 0.9 });
  put(out, anchorIndex(16, 11, 10), { cx: 172, cy: 170, w: 100, h: 60, obj: 0.8, cls: 7, p: 0.8 }); // stesso veicolo
  put(out, anchorIndex(16, 20, 10), { cx: 330, cy: 168, w: 100, h: 60, obj: 0.7, cls: 2, p: 0.9 }); // un altro
  const d = decodeOutput(out, grid, NC, { x: 0, y: 0, w: SIZE, h: SIZE }, 1);
  assert.equal(d.length, 2);
  assert.equal(d[0].cls, 0);
  assert.ok(Math.abs(d[0].score - 0.81) < 1e-6);
});

test('riquadri limitati alla zona analizzata', () => {
  const out = new Float32Array(anchors * STRIDE);
  put(out, anchorIndex(32, 0, 0), { cx: 10, cy: 10, w: 100, h: 100, obj: 0.9, cls: 2, p: 0.9 });
  const roi = { x: 200, y: 300, w: SIZE, h: SIZE };
  const [d] = decodeOutput(out, grid, NC, roi, 1);
  assert.equal(d.x, 200);
  assert.equal(d.y, 300);
  assert.ok(Math.abs(d.w - 60) < 1e-3);
});

test('zona in pixel interi come Roi.From', () => {
  assert.deepEqual(roiFrom(null, 1920, 1080), { x: 0, y: 0, w: 1920, h: 1080 });
  assert.deepEqual(roiFrom({ x: -5.5, y: 10.2, w: 100.1, h: 2000 }, 1920, 1080), { x: 0, y: 10, w: 95, h: 1070 });
});

test('letterbox: proporzioni mantenute', () => {
  assert.deepEqual(letterboxScale({ w: 1920, h: 1080 }, 640), { scale: 1 / 3, outW: 640, outH: 360 });
  assert.deepEqual(letterboxScale({ w: 432, h: 768 }, 416), { scale: 416 / 768, outW: 234, outH: 416 });
});

test('pixel RGBA convertiti in piani B, G, R', () => {
  const rgba = new Uint8ClampedArray([10, 20, 30, 255, 40, 50, 60, 255, 1, 2, 3, 255, 7, 8, 9, 255]);
  const t = rgbaToTensor(rgba, 2, new Float32Array(12));
  assert.deepEqual(Array.from(t), [30, 60, 3, 9, 20, 50, 2, 8, 10, 40, 1, 7]);
});

test('IoU e NMS', () => {
  const a = { x: 0, y: 0, w: 10, h: 10 }, b = { x: 5, y: 0, w: 10, h: 10 };
  assert.ok(Math.abs(iou(a, b) - 50 / 150) < 1e-12);
  assert.equal(nms([{ ...a, score: 0.5 }, { ...b, score: 0.9 }], 0.3).length, 1);
  assert.equal(nms([{ ...a, score: 0.5 }, { ...b, score: 0.9 }], 0.4).length, 2);
});

test('confronto GPU/CPU: uguali, rumore minimo, valori non validi', () => {
  const e = new Float32Array(85 * 10).map((_, i) => (i % 85 >= 4 ? 0.5 : 100));
  assert.ok(outputsMatch(e, e.slice()));
  const noisy = e.map(v => v + 0.001);
  assert.ok(outputsMatch(e, noisy));
  const nan = e.slice(); nan[7] = NaN;
  assert.equal(outputsMatch(e, nan), false);
  const wrongScore = e.slice(); wrongScore[90] = 0.9;
  assert.equal(outputsMatch(e, wrongScore), false);
  assert.equal(outputsMatch(e, new Float32Array(10)), false);
});

test('immagine di prova deterministica e nell\'intervallo 0–255', () => {
  const a = validationInput(64), b = validationInput(64);
  assert.deepEqual(a, b);
  assert.ok(a.every(v => v >= 0 && v < 256));
});

// ---------------------------------------------------------------- scelta dell'esecutore
// ONNX Runtime finto: verifica solo la logica di scelta e di ripiego, non il modello.
import { createDetector } from '../../src/TrafficSpeedAnalyzer.Web/wwwroot/js/yolox.js';

function fakeOrt({ gpuFailAfter = Infinity, gpuWrong = false } = {}) {
  const released = [];
  const out = new Float32Array(8400 * 85);
  const make = (ep) => {
    let runs = 0;
    return {
      ep, inputNames: ['images'], outputNames: ['output'],
      async run() {
        runs++;
        if (ep === 'webgpu' && runs > gpuFailAfter) throw new Error('[WebGPU] Kernel "[Conv] Conv_41_token_1" failed. Error: no GPU data for input: 183');
        const data = ep === 'webgpu' && gpuWrong ? out.map(() => NaN) : out;
        return { output: { data, dims: [1, 8400, 85] } };
      },
      async release() { released.push(ep); },
    };
  };
  return {
    released,
    env: { wasm: { numThreads: 1 } },
    Tensor: class { constructor(type, data, dims) { Object.assign(this, { type, data, dims }); } },
    InferenceSession: { async create(_b, o) { return make(o.executionProviders[0]); } },
  };
}

function withCanvas(fn) {
  return async () => {
    const saved = [globalThis.OffscreenCanvas, Object.getOwnPropertyDescriptor(globalThis, 'navigator')];
    globalThis.OffscreenCanvas = class {
      constructor(w, h) { this.width = w; this.height = h; }
      getContext() { const c = this; return { fillRect() {}, drawImage() {}, getImageData: () => ({ data: new Uint8ClampedArray(c.width * c.height * 4) }) }; }
    };
    Object.defineProperty(globalThis, 'navigator', { value: { gpu: {} }, configurable: true });
    try { await fn(); } finally {
      globalThis.OffscreenCanvas = saved[0];
      if (saved[1]) Object.defineProperty(globalThis, 'navigator', saved[1]);
    }
  };
}

const src = { width: 640, height: 640 };

test('WebGPU scelto se coincide con la CPU; la sessione CPU non viene rilasciata', withCanvas(async () => {
  const ort = fakeOrt();
  const d = await createDetector(new Uint8Array(1), { size: 640, ort });
  assert.equal(d.provider, 'GPU (WebGPU)');
  assert.deepEqual(ort.released, []);
  await d.detect(src, null);
  assert.equal(d.gpuFailures, 0);
}));

test('WebGPU con uscite non valide: si usa la CPU', withCanvas(async () => {
  const d = await createDetector(new Uint8Array(1), { size: 640, ort: fakeOrt({ gpuWrong: true }) });
  assert.equal(d.provider, 'CPU (WebAssembly)');
  assert.match(d.note, /non validi/);
}));

test('WebGPU che fallisce durante l\'analisi: fotogramma rifatto su CPU e poi solo CPU', withCanvas(async () => {
  const d = await createDetector(new Uint8Array(1), { size: 640, ort: fakeOrt({ gpuFailAfter: 3 }) });
  assert.equal(d.provider, 'GPU (WebGPU)');
  await d.detect(src, null);                 // terza esecuzione GPU: ok
  const dets = await d.detect(src, null);    // quarta: errore → CPU
  assert.deepEqual(dets, []);
  assert.equal(d.gpuFailures, 1);
  assert.equal(d.provider, 'CPU (WebAssembly)');
  assert.match(d.note, /no GPU data/);
  await d.detect(src, null);
  assert.equal(d.gpuFailures, 1);
}));
