// Decodifica con WebCodecs (accelerazione hardware quando disponibile).
// Ogni fotogramma decodificato porta il pts reale letto dal file: il campo timestamp del
// VideoFrame (microsecondi) serve solo a ritrovare l'indice del fotogramma.
import { readSample } from './mp4demux.js';

export function decoderConfig(video, hardwareAcceleration = 'no-preference') {
  const config = {
    codec: video.codec,
    codedWidth: video.codedWidth,
    codedHeight: video.codedHeight,
    hardwareAcceleration,
    optimizeForLatency: false,
  };
  if (video.description) config.description = video.description;
  return config;
}

/** Verifica che il browser sappia decodificare il video; restituisce la configurazione da usare. */
export async function checkSupport(video) {
  if (typeof VideoDecoder === 'undefined') {
    return { supported: false, reason: 'Questo browser non supporta WebCodecs. Usa Chrome o Edge aggiornati.' };
  }
  for (const hw of ['prefer-hardware', 'no-preference']) {
    const config = decoderConfig(video, hw);
    try {
      const r = await VideoDecoder.isConfigSupported(config);
      if (r.supported) return { supported: true, config, hardware: hw === 'prefer-hardware' };
    } catch { /* configurazione non valida: si prova la successiva */ }
  }
  const hevc = video.entryType === 'hvc1' || video.entryType === 'hev1';
  return {
    supported: false,
    reason: hevc
      ? 'Questo browser non decodifica HEVC (H.265). Usa Chrome o Edge su Windows con decodifica hardware, oppure converti il video in H.264.'
      : `Formato video non supportato dal browser (${video.codec}).`,
  };
}

function tsOf(video, sample) { return Math.round(video.pts[sample] * 1e6); }

async function chunk(video, sample) {
  const data = await readSample(video, sample);
  return new EncodedVideoChunk({
    type: video.samples.keys[sample] ? 'key' : 'delta',
    timestamp: tsOf(video, sample),
    duration: Math.round(video.samples.durations[sample] / video.timescale * 1e6),
    data,
  });
}

/**
 * Intervallo di campioni (in ordine di decodifica) necessario per ottenere i fotogrammi
 * [firstFrame, lastFrame]: dal fotogramma chiave precedente fino a quello successivo,
 * perché con i B-frame un fotogramma può dipendere da campioni decodificati dopo.
 */
export function decodeRange(video, firstFrame, lastFrame) {
  const { keys, count } = video.samples;
  let minSample = Infinity, maxSample = -1;
  for (let f = firstFrame; f <= lastFrame; f++) {
    const s = video.order[f];
    if (s < minSample) minSample = s;
    if (s > maxSample) maxSample = s;
  }
  let start = minSample;
  while (start > 0 && !keys[start]) start--;
  let end = maxSample + 1;
  while (end < count && !keys[end]) end++;
  return { start, end: end - 1 };
}

/**
 * Fotogrammi [firstFrame, lastFrame] in ordine di presentazione, uno alla volta.
 * Chi riceve { frame, index, time } deve chiamare frame.close() appena ha finito.
 */
export async function* frames(video, config, firstFrame, lastFrame, signal) {
  const tsToFrame = new Map();
  for (let s = 0; s < video.samples.count; s++) tsToFrame.set(tsOf(video, s), video.frameOfSample[s]);

  const queue = [];
  let failure = null, finished = false, wake = null;
  const notify = () => { const w = wake; wake = null; w?.(); };
  const decoder = new VideoDecoder({
    output: f => { queue.push(f); notify(); },
    error: e => { failure = e; notify(); },
  });
  decoder.addEventListener('dequeue', notify);
  decoder.configure(config);

  const { start, end } = decodeRange(video, firstFrame, lastFrame);
  let next = start, flushing = false;
  try {
    while (true) {
      if (signal?.aborted) return;
      if (failure) throw failure;
      // Alimenta il decodificatore senza accumulare troppi fotogrammi in memoria.
      while (!flushing && next <= end && queue.length < 4 && decoder.decodeQueueSize < 8) {
        decoder.decode(await chunk(video, next++));
      }
      if (!flushing && next > end) {
        flushing = true;
        decoder.flush().then(() => { finished = true; notify(); }, e => { failure ??= e; notify(); });
      }
      if (queue.length) {
        const frame = queue.shift();
        const index = tsToFrame.get(frame.timestamp);
        if (index === undefined || index < firstFrame || index > lastFrame) { frame.close(); continue; }
        yield { frame, index, time: video.frameTimes[index] };
        continue;
      }
      if (finished) return;
      await new Promise(r => { wake = r; });
    }
  } finally {
    for (const f of queue) f.close();
    if (decoder.state !== 'closed') decoder.close();
  }
}

/** Un singolo fotogramma (per l'anteprima e le immagini di ingresso/uscita). */
export async function frameAt(video, config, index) {
  for await (const item of frames(video, config, index, index)) return item;
  throw new Error(`Fotogramma ${index} non decodificato.`);
}

/**
 * Disegna il fotogramma nel canvas con le dimensioni di visualizzazione, applicando la
 * rotazione indicata nel file (come fa FFmpeg). Le coordinate di linee e riquadri sono
 * sempre quelle del video visualizzato.
 */
export function drawFrame(ctx, frame, video) {
  const w = video.displayWidth, h = video.displayHeight;
  if (ctx.canvas.width !== w || ctx.canvas.height !== h) { ctx.canvas.width = w; ctx.canvas.height = h; }
  const cw = frame.displayWidth, ch = frame.displayHeight;
  ctx.save();
  switch (video.rotation) {
    case 90: ctx.setTransform(0, 1, -1, 0, w, 0); break;
    case 180: ctx.setTransform(-1, 0, 0, -1, w, h); break;
    case 270: ctx.setTransform(0, -1, 1, 0, 0, h); break;
    default: ctx.setTransform(1, 0, 0, 1, 0, 0);
  }
  ctx.drawImage(frame, 0, 0, cw, ch);
  ctx.restore();
}
