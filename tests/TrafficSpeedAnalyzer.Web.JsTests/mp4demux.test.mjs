// Test del demuxer MP4 del prototipo web: confronto campione per campione con FFprobe.
// Esecuzione: node --test tests/TrafficSpeedAnalyzer.Web.JsTests
// I file *.ffprobe.json sono stati prodotti con:
//   ffprobe -select_streams v:0 -show_packets -show_entries packet=pts_time,dts_time,size,pos,flags ...
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync, readdirSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { openMp4, bufferSource, codecString, normalizeIsoOffset } from '../../src/TrafficSpeedAnalyzer.Web/wwwroot/js/mp4demux.js';

const here = dirname(fileURLToPath(import.meta.url));
const fixtures = join(here, 'fixtures');
const videos = readdirSync(fixtures).filter(f => /\.(mp4|mov)$/i.test(f));

async function open(name) {
  const u8 = new Uint8Array(readFileSync(join(fixtures, name)));
  return openMp4(bufferSource(u8, name));
}

for (const name of videos) {
  test(`${name}: campioni identici a FFprobe`, async () => {
    const expected = JSON.parse(readFileSync(join(fixtures, name + '.ffprobe.json'), 'utf8'));
    const v = await open(name);
    assert.equal(v.frameCount, expected.packets.length);
    assert.equal(v.codedWidth, expected.width);
    assert.equal(v.codedHeight, expected.height);
    assert.equal(v.rotation, expected.rotation);
    const s = v.samples;
    for (let i = 0; i < expected.packets.length; i++) {
      const p = expected.packets[i];
      assert.ok(Math.abs(v.pts[i] - p.pts) < 1e-6, `pts del campione ${i}: ${v.pts[i]} invece di ${p.pts}`);
      assert.equal(s.sizes[i], p.size, `dimensione del campione ${i}`);
      assert.equal(s.offsets[i], p.pos, `posizione del campione ${i}`);
      assert.equal(!!s.keys[i], p.key, `fotogramma chiave ${i}`);
    }
  });

  test(`${name}: fotogrammi in ordine di presentazione`, async () => {
    const expected = JSON.parse(readFileSync(join(fixtures, name + '.ffprobe.json'), 'utf8'));
    const v = await open(name);
    const sorted = expected.packets.map(p => p.pts).sort((a, b) => a - b);
    for (let f = 0; f < sorted.length; f++) {
      assert.ok(Math.abs(v.frameTimes[f] - sorted[f]) < 1e-6, `fotogramma ${f}`);
      assert.equal(v.frameOfSample[v.order[f]], f);
    }
  });
}

test('rotazione e dimensioni di visualizzazione (HEVC verticale)', async () => {
  const v = await open('hevc_bframes_rot90.mp4');
  assert.equal(v.entryType, 'hvc1');
  assert.match(v.codec, /^hvc1\.1\.6\.L\d+/);
  assert.equal(v.rotation, 90);
  assert.equal(v.displayWidth, 240);
  assert.equal(v.displayHeight, 320);
});

test('codec H.264 e frequenza dei fotogrammi del campione', async () => {
  const v = await open('h264_known_speed.mp4');
  assert.equal(v.codec, 'avc1.64001e');
  assert.ok(Math.abs(v.meanFps - 30) < 1e-6);
  assert.ok(Math.abs(v.duration - 8) < 1e-6);
  assert.equal(v.creationDate, '2026-10-26T07:00:00.000Z');
});

test('data di creazione Apple con fuso orario', async () => {
  const v = await open('apple_date.mov');
  assert.equal(v.creationDate, '2026-10-25T18:12:34+02:00');
  assert.equal(v.creationSource, 'com.apple.quicktime.creationdate');
});

test('stringa del codec HEVC da hvcC', () => {
  // Main profile, compatibilità 0x60000000, livello 93, vincoli B0.
  const cfg = new Uint8Array(23);
  cfg[1] = 0x01; cfg[2] = 0x60; cfg[6] = 0xb0; cfg[12] = 93;
  assert.equal(codecString('hvc1', cfg), 'hvc1.1.6.L93.B0');
  cfg[1] = 0x22; // tier alto, profilo 2 (Main 10)
  assert.equal(codecString('hev1', cfg), 'hev1.2.6.H93.B0');
});

test('fuso orario normalizzato', () => {
  assert.equal(normalizeIsoOffset('2026-10-25T18:12:34+0200'), '2026-10-25T18:12:34+02:00');
  assert.equal(normalizeIsoOffset('2026-10-25T18:12:34Z'), '2026-10-25T18:12:34Z');
});

test('file non MP4 rifiutato con un messaggio chiaro', async () => {
  const junk = new Uint8Array(64).fill(7);
  await assert.rejects(openMp4(bufferSource(junk, 'x.mp4')), /MP4|moov/);
});
