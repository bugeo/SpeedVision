// Lettura dei file MP4/MOV (ISO BMFF) direttamente dal disco dell'utente, senza librerie esterne.
// Legge solo l'indice ("moov") e, durante la decodifica, i singoli campioni: anche un file di
// molti GB non viene mai caricato per intero in memoria.
//
// Restituisce per la traccia video: codec per WebCodecs, descrizione (avcC/hvcC), dimensioni,
// rotazione, data di creazione e la tabella dei campioni con timestamp reali (pts) derivati da
// stts/ctts ed edit list, come fa FFprobe.

/** Sorgente di byte: un File/Blob del browser oppure, nei test, un buffer in memoria. */
export function blobSource(blob) {
  return {
    size: blob.size,
    name: blob.name ?? '',
    async read(offset, length) {
      const end = Math.min(blob.size, offset + length);
      return new Uint8Array(await blob.slice(offset, end).arrayBuffer());
    },
  };
}

export function bufferSource(u8, name = '') {
  return {
    size: u8.length,
    name,
    async read(offset, length) {
      return u8.subarray(offset, Math.min(u8.length, offset + length));
    },
  };
}

const CONTAINERS = new Set(['moov', 'trak', 'mdia', 'minf', 'stbl', 'edts', 'udta', 'dinf']);
const VIDEO_ENTRIES = new Set(['avc1', 'avc3', 'hvc1', 'hev1', 'vp09', 'av01']);

class Reader {
  constructor(u8, pos = 0, end = u8.length) {
    this.u8 = u8;
    this.dv = new DataView(u8.buffer, u8.byteOffset, u8.byteLength);
    this.pos = pos;
    this.end = end;
  }
  u8at(p) { return this.u8[p]; }
  u16() { const v = this.dv.getUint16(this.pos); this.pos += 2; return v; }
  u32() { const v = this.dv.getUint32(this.pos); this.pos += 4; return v; }
  i32() { const v = this.dv.getInt32(this.pos); this.pos += 4; return v; }
  u64() { const hi = this.u32(); const lo = this.u32(); return hi * 4294967296 + lo; }
  i64() { const hi = this.i32(); const lo = this.u32(); return hi * 4294967296 + lo; }
  type() { const s = String.fromCharCode(...this.u8.subarray(this.pos, this.pos + 4)); this.pos += 4; return s; }
  skip(n) { this.pos += n; }
  bytes(n) { const b = this.u8.subarray(this.pos, this.pos + n); this.pos += n; return b; }
}

/** Elenca i box figli nell'intervallo [start, end) del buffer. */
function* boxes(u8, start, end) {
  const r = new Reader(u8, start, end);
  while (r.pos + 8 <= end) {
    const at = r.pos;
    let size = r.u32();
    const type = r.type();
    let header = 8;
    if (size === 1) { size = r.u64(); header = 16; } else if (size === 0) { size = end - at; }
    if (size < header || at + size > end) break; // box troncato o corrotto: ci si ferma
    yield { type, start: at, body: at + header, end: at + size };
    r.pos = at + size;
  }
}

function child(u8, box, type) {
  for (const b of boxes(u8, box.body, box.end)) if (b.type === type) return b;
  return null;
}

/** Cerca i box di primo livello leggendo solo le intestazioni. */
async function findTopLevel(src) {
  const found = [];
  let pos = 0;
  while (pos + 8 <= src.size) {
    const h = await src.read(pos, 16);
    const dv = new DataView(h.buffer, h.byteOffset, h.byteLength);
    let size = dv.getUint32(0);
    const type = String.fromCharCode(h[4], h[5], h[6], h[7]);
    let header = 8;
    if (size === 1) { size = dv.getUint32(8) * 4294967296 + dv.getUint32(12); header = 16; }
    else if (size === 0) { size = src.size - pos; }
    if (size < header) throw new Error('Struttura MP4 non valida.');
    found.push({ type, start: pos, size, header });
    pos += size;
  }
  return found;
}

const MAC_EPOCH_OFFSET = 2082844800; // secondi tra 1904-01-01 e 1970-01-01

function parseMvhd(u8, box) {
  const r = new Reader(u8, box.body);
  const version = r.u8at(r.pos); r.skip(4);
  let creation, timescale, duration;
  if (version === 1) { creation = r.u64(); r.u64(); timescale = r.u32(); duration = r.u64(); }
  else { creation = r.u32(); r.u32(); timescale = r.u32(); duration = r.u32(); }
  return { creation, timescale, duration };
}

function parseTkhd(u8, box) {
  const r = new Reader(u8, box.body);
  const version = r.u8at(r.pos);
  r.pos = box.body + (version === 1 ? 52 : 40);
  const m = [];
  for (let i = 0; i < 9; i++) m.push(r.i32());
  const width = r.u32() / 65536;
  const height = r.u32() / 65536;
  // Matrice a b u / c d v / x y w (a, b, c, d in 16.16): angolo di rotazione in senso orario.
  const a = m[0] / 65536, b = m[1] / 65536;
  let rotation = Math.round(Math.atan2(b, a) * 180 / Math.PI);
  rotation = ((Math.round(rotation / 90) * 90) % 360 + 360) % 360;
  return { width, height, rotation };
}

function parseMdhd(u8, box) {
  const r = new Reader(u8, box.body);
  const version = r.u8at(r.pos); r.skip(4);
  if (version === 1) { r.u64(); r.u64(); return { timescale: r.u32(), duration: r.u64() }; }
  r.u32(); r.u32();
  return { timescale: r.u32(), duration: r.u32() };
}

function parseElst(u8, box) {
  const r = new Reader(u8, box.body);
  const version = r.u8at(r.pos); r.skip(4);
  const n = r.u32();
  const list = [];
  for (let i = 0; i < n; i++) {
    const segmentDuration = version === 1 ? r.u64() : r.u32();
    const mediaTime = version === 1 ? r.i64() : r.i32();
    r.skip(4); // media_rate
    list.push({ segmentDuration, mediaTime });
  }
  return list;
}

function hex2(v) { return v.toString(16).padStart(2, '0'); }

function reverseBits32(v) {
  let r = 0;
  for (let i = 0; i < 32; i++) { r = (r * 2) + (v & 1); v >>>= 1; }
  return r >>> 0;
}

/** Stringa del codec secondo RFC 6381, come la richiede WebCodecs. */
export function codecString(entryType, config) {
  if (entryType === 'avc1' || entryType === 'avc3') {
    return `${entryType}.${hex2(config[1])}${hex2(config[2])}${hex2(config[3])}`;
  }
  if (entryType === 'hvc1' || entryType === 'hev1') {
    const b1 = config[1];
    const space = ['', 'A', 'B', 'C'][b1 >> 6];
    const tier = (b1 >> 5) & 1 ? 'H' : 'L';
    const profile = b1 & 0x1f;
    const compat = ((config[2] << 24) | (config[3] << 16) | (config[4] << 8) | config[5]) >>> 0;
    const level = config[12];
    const constraints = Array.from(config.subarray(6, 12));
    while (constraints.length && constraints[constraints.length - 1] === 0) constraints.pop();
    let s = `${entryType}.${space}${profile}.${reverseBits32(compat).toString(16)}.${tier}${level}`;
    for (const c of constraints) s += '.' + c.toString(16).toUpperCase();
    return s;
  }
  return entryType;
}

function parseStsd(u8, box) {
  const r = new Reader(u8, box.body + 8); // versione/flag + numero di voci
  const size = r.u32();
  const type = r.type();
  const entry = { type, start: r.pos - 8, end: r.pos - 8 + size };
  if (!VIDEO_ENTRIES.has(type)) return { type };
  r.skip(6 + 2 + 16);
  const codedWidth = r.u16();
  const codedHeight = r.u16();
  const childStart = entry.start + 8 + 78;
  let description = null;
  let configType = null;
  for (const b of boxes(u8, childStart, entry.end)) {
    if (b.type === 'avcC' || b.type === 'hvcC' || b.type === 'vpcC' || b.type === 'av1C') {
      description = u8.slice(b.body, b.end);
      configType = b.type;
    }
  }
  if (!description && (type.startsWith('avc') || type.startsWith('h'))) {
    throw new Error('Configurazione del codec mancante nel file.');
  }
  return { type, codedWidth, codedHeight, description, configType };
}

/** Tabella dei campioni in ordine di decodifica. */
function buildSamples(u8, stbl) {
  const get = t => child(u8, stbl, t);
  const stts = get('stts'), stsz = get('stsz'), stsc = get('stsc');
  const stco = get('stco'), co64 = get('co64'), ctts = get('ctts'), stss = get('stss');
  if (!stts || !stsz || !stsc || !(stco || co64)) throw new Error('Indice dei campioni incompleto (file non standard o frammentato).');

  let r = new Reader(u8, stsz.body + 4);
  const fixedSize = r.u32();
  const count = r.u32();
  const sizes = new Uint32Array(count);
  for (let i = 0; i < count; i++) sizes[i] = fixedSize || r.u32();

  const dts = new Float64Array(count);
  const durations = new Float64Array(count);
  r = new Reader(u8, stts.body + 4);
  let n = r.u32(), k = 0, t = 0;
  for (let e = 0; e < n; e++) {
    const c = r.u32(), d = r.u32();
    for (let j = 0; j < c && k < count; j++, k++) { dts[k] = t; durations[k] = d; t += d; }
  }

  const cts = new Float64Array(count);
  cts.set(dts);
  if (ctts) {
    r = new Reader(u8, ctts.body);
    const version = r.u8at(r.pos); r.skip(4);
    n = r.u32(); k = 0;
    for (let e = 0; e < n; e++) {
      const c = r.u32();
      const off = version === 1 ? r.i32() : r.u32();
      for (let j = 0; j < c && k < count; j++, k++) cts[k] = dts[k] + off;
    }
  }

  const keys = new Uint8Array(count);
  if (stss) {
    r = new Reader(u8, stss.body + 4);
    n = r.u32();
    for (let e = 0; e < n; e++) { const s = r.u32() - 1; if (s >= 0 && s < count) keys[s] = 1; }
  } else {
    keys.fill(1);
  }

  const chunkOffsets = [];
  if (stco) { r = new Reader(u8, stco.body + 4); n = r.u32(); for (let i = 0; i < n; i++) chunkOffsets.push(r.u32()); }
  else { r = new Reader(u8, co64.body + 4); n = r.u32(); for (let i = 0; i < n; i++) chunkOffsets.push(r.u64()); }

  r = new Reader(u8, stsc.body + 4);
  n = r.u32();
  const stscEntries = [];
  for (let e = 0; e < n; e++) stscEntries.push({ first: r.u32(), perChunk: r.u32(), sdi: r.u32() });

  const offsets = new Float64Array(count);
  let s = 0;
  for (let e = 0; e < stscEntries.length && s < count; e++) {
    const firstChunk = stscEntries[e].first - 1;
    const lastChunk = e + 1 < stscEntries.length ? stscEntries[e + 1].first - 2 : chunkOffsets.length - 1;
    for (let c = firstChunk; c <= lastChunk && s < count; c++) {
      let off = chunkOffsets[c];
      for (let j = 0; j < stscEntries[e].perChunk && s < count; j++, s++) { offsets[s] = off; off += sizes[s]; }
    }
  }

  return { count, sizes, dts, cts, durations, keys, offsets };
}

/** Legge le chiavi QuickTime (moov/meta) cercando com.apple.quicktime.creationdate. */
function appleCreationDate(u8, moov) {
  // iPhone: moov/meta; FFmpeg e altri programmi: moov/udta/meta.
  const udta = child(u8, moov, 'udta');
  for (const meta of [child(u8, moov, 'meta'), udta && child(u8, udta, 'meta')]) {
    const value = meta && appleKeyValue(u8, meta, 'com.apple.quicktime.creationdate');
    if (value) return value;
  }
  return null;
}

function appleKeyValue(u8, meta, wanted) {
  // Nei file QuickTime "meta" non ha versione/flag; nei file ISO sì (4 byte a zero).
  let start = meta.body;
  const first = new DataView(u8.buffer, u8.byteOffset + start, 8);
  if (first.getUint32(0) === 0) start += 4;
  const box = { body: start, end: meta.end };
  const keysBox = child(u8, box, 'keys');
  const ilst = child(u8, box, 'ilst');
  if (!keysBox || !ilst) return null;
  const r = new Reader(u8, keysBox.body + 4);
  const n = r.u32();
  const names = [];
  for (let i = 0; i < n; i++) {
    const size = r.u32();
    r.type(); // namespace "mdta"
    names.push(new TextDecoder().decode(r.bytes(size - 8)));
  }
  for (const item of boxes(u8, ilst.body, ilst.end)) {
    const idx = new DataView(u8.buffer, u8.byteOffset + item.start + 4, 4).getUint32(0);
    if (names[idx - 1] !== wanted) continue;
    const data = child(u8, item, 'data');
    if (!data) continue;
    return new TextDecoder().decode(u8.subarray(data.body + 8, data.end)).trim();
  }
  return null;
}

/** Normalizza "2026-10-25T18:12:34+0200" in ISO 8601 con i due punti nel fuso. */
export function normalizeIsoOffset(s) {
  if (!s) return null;
  return s.replace(/([+-]\d{2})(\d{2})$/, '$1:$2');
}

/**
 * Apre il file e ne legge l'indice.
 * @returns {Promise<object>} informazioni sulla traccia video e tabella dei fotogrammi.
 */
export async function openMp4(src) {
  const top = await findTopLevel(src);
  const moovInfo = top.find(b => b.type === 'moov');
  if (!moovInfo) throw new Error('Indice "moov" non trovato: il file non è un MP4/MOV completo.');
  if (top.some(b => b.type === 'moof')) throw new Error('I file MP4 frammentati non sono ancora supportati.');
  if (moovInfo.size > 512 * 1024 * 1024) throw new Error('Indice del video troppo grande.');

  const u8 = await src.read(moovInfo.start, moovInfo.size);
  const moov = { body: moovInfo.header, end: u8.length };
  const mvhdBox = child(u8, moov, 'mvhd');
  if (!mvhdBox) throw new Error('Intestazione del filmato mancante.');
  const mvhd = parseMvhd(u8, mvhdBox);

  let video = null;
  for (const trak of boxes(u8, moov.body, moov.end)) {
    if (trak.type !== 'trak') continue;
    const mdia = child(u8, trak, 'mdia');
    const hdlr = mdia && child(u8, mdia, 'hdlr');
    if (!hdlr) continue;
    const handler = String.fromCharCode(...u8.subarray(hdlr.body + 8, hdlr.body + 12));
    if (handler !== 'vide') continue;
    const minf = child(u8, mdia, 'minf');
    const stbl = minf && child(u8, minf, 'stbl');
    const stsd = stbl && child(u8, stbl, 'stsd');
    if (!stsd) continue;
    const entry = parseStsd(u8, stsd);
    if (!VIDEO_ENTRIES.has(entry.type)) continue;
    const tkhd = parseTkhd(u8, child(u8, trak, 'tkhd'));
    const mdhd = parseMdhd(u8, child(u8, mdia, 'mdhd'));
    const edts = child(u8, trak, 'edts');
    const elst = edts && child(u8, edts, 'elst');
    video = { entry, tkhd, mdhd, elst: elst ? parseElst(u8, elst) : [], samples: buildSamples(u8, stbl) };
    break;
  }
  if (!video) throw new Error('Nessuna traccia video trovata nel file.');

  const { entry, tkhd, mdhd, elst, samples } = video;
  const ts = mdhd.timescale;

  // Edit list: come FFmpeg, pts = (cts − media_time) / timescale + eventuale ritardo iniziale vuoto.
  let mediaTime = 0, emptyDelay = 0;
  for (const e of elst) {
    if (e.mediaTime === -1) { emptyDelay += e.segmentDuration / mvhd.timescale; continue; }
    mediaTime = e.mediaTime;
    break;
  }

  const pts = new Float64Array(samples.count);
  for (let i = 0; i < samples.count; i++) pts[i] = (samples.cts[i] - mediaTime) / ts + emptyDelay;

  // Ordine di presentazione: indice del fotogramma come nell'app desktop (ordinati per pts).
  const order = Array.from({ length: samples.count }, (_, i) => i).sort((a, b) => pts[a] - pts[b] || a - b);
  const frameOfSample = new Int32Array(samples.count);
  order.forEach((s, f) => { frameOfSample[s] = f; });
  const frameTimes = new Float64Array(samples.count);
  order.forEach((s, f) => { frameTimes[f] = pts[s]; });

  const rotation = tkhd.rotation;
  const codedWidth = entry.codedWidth, codedHeight = entry.codedHeight;
  const swap = rotation === 90 || rotation === 270;

  const apple = normalizeIsoOffset(appleCreationDate(u8, moov));
  const mvhdDate = mvhd.creation > MAC_EPOCH_OFFSET
    ? new Date((mvhd.creation - MAC_EPOCH_OFFSET) * 1000).toISOString()
    : null;

  let minDt = Infinity, maxDt = 0;
  for (let f = 1; f < frameTimes.length; f++) {
    const d = frameTimes[f] - frameTimes[f - 1];
    if (d < minDt) minDt = d;
    if (d > maxDt) maxDt = d;
  }
  const span = frameTimes.length > 1 ? frameTimes[frameTimes.length - 1] - frameTimes[0] : 0;

  return {
    source: src,
    name: src.name,
    fileSize: src.size,
    codec: codecString(entry.type, entry.description ?? new Uint8Array(16)),
    entryType: entry.type,
    description: entry.description,
    codedWidth,
    codedHeight,
    rotation,
    displayWidth: swap ? codedHeight : codedWidth,
    displayHeight: swap ? codedWidth : codedHeight,
    timescale: ts,
    frameCount: samples.count,
    duration: span + (frameTimes.length ? samples.durations[order[order.length - 1]] / ts : 0),
    meanFps: span > 0 ? (frameTimes.length - 1) / span : 0,
    minFrameInterval: Number.isFinite(minDt) ? minDt : 0,
    maxFrameInterval: maxDt,
    creationDate: apple ?? mvhdDate,
    creationSource: apple ? 'com.apple.quicktime.creationdate' : (mvhdDate ? 'mvhd (UTC)' : null),
    samples,
    pts,
    order,          // order[frame] = campione
    frameOfSample,  // frameOfSample[campione] = fotogramma
    frameTimes,     // pts reale di ogni fotogramma, in secondi
  };
}

/** Byte del campione (in ordine di decodifica). */
export function readSample(video, sampleIndex) {
  const s = video.samples;
  return video.source.read(s.offsets[sampleIndex], s.sizes[sampleIndex]);
}
