// Distanza tra due punti sull'ellissoide WGS84 (formula inversa di Vincenty): la stessa di GeoMath.cs,
// così la distanza mostrata sulla mappa è quella che il programma userà.
(function (root) {
  'use strict';
  const A = 6378137.0, F = 1 / 298.257223563, B = A * (1 - F);
  const rad = d => d * Math.PI / 180;

  function haversine(p, q) {
    const r = 6371008.8, dLat = rad(q.lat - p.lat), dLon = rad(q.lon - p.lon);
    const h = Math.sin(dLat / 2) ** 2 + Math.cos(rad(p.lat)) * Math.cos(rad(q.lat)) * Math.sin(dLon / 2) ** 2;
    return 2 * r * Math.asin(Math.min(1, Math.sqrt(h)));
  }

  /** Metri tra p e q ({lat, lon} in gradi). */
  function distanceMeters(p, q) {
    const L = rad(q.lon - p.lon);
    const U1 = Math.atan((1 - F) * Math.tan(rad(p.lat))), U2 = Math.atan((1 - F) * Math.tan(rad(q.lat)));
    const sinU1 = Math.sin(U1), cosU1 = Math.cos(U1), sinU2 = Math.sin(U2), cosU2 = Math.cos(U2);
    let lambda = L, sinSigma, cosSigma, sigma, cos2Alpha, cos2SigmaM;
    for (let i = 0; ; i++) {
      const sinL = Math.sin(lambda), cosL = Math.cos(lambda);
      const t1 = cosU2 * sinL, t2 = cosU1 * sinU2 - sinU1 * cosU2 * cosL;
      sinSigma = Math.sqrt(t1 * t1 + t2 * t2);
      if (sinSigma === 0) return 0;
      cosSigma = sinU1 * sinU2 + cosU1 * cosU2 * cosL;
      sigma = Math.atan2(sinSigma, cosSigma);
      const sinAlpha = cosU1 * cosU2 * sinL / sinSigma;
      cos2Alpha = 1 - sinAlpha * sinAlpha;
      cos2SigmaM = cos2Alpha === 0 ? 0 : cosSigma - 2 * sinU1 * sinU2 / cos2Alpha;
      const C = F / 16 * cos2Alpha * (4 + F * (4 - 3 * cos2Alpha));
      const prev = lambda;
      lambda = L + (1 - C) * F * sinAlpha * (sigma + C * sinSigma * (cos2SigmaM + C * cosSigma * (-1 + 2 * cos2SigmaM * cos2SigmaM)));
      if (Math.abs(lambda - prev) < 1e-12) break;
      if (i > 200) return haversine(p, q);
    }
    const uSq = cos2Alpha * (A * A - B * B) / (B * B);
    const bigA = 1 + uSq / 16384 * (4096 + uSq * (-768 + uSq * (320 - 175 * uSq)));
    const bigB = uSq / 1024 * (256 + uSq * (-128 + uSq * (74 - 47 * uSq)));
    const dSigma = bigB * sinSigma * (cos2SigmaM + bigB / 4 * (cosSigma * (-1 + 2 * cos2SigmaM * cos2SigmaM)
      - bigB / 6 * cos2SigmaM * (-3 + 4 * sinSigma * sinSigma) * (-3 + 4 * cos2SigmaM * cos2SigmaM)));
    return B * bigA * (sigma - dSigma);
  }

  /** Metri rappresentati da un pixel della mappa (tasselli da 256 px) alla latitudine e all'ingrandimento dati. */
  function metersPerPixel(lat, zoom) {
    return 40075016.686 * Math.cos(rad(lat)) / (256 * Math.pow(2, zoom));
  }

  const api = { distanceMeters, metersPerPixel };
  if (typeof module !== 'undefined' && module.exports) module.exports = api;
  else root.TsaGeo = api;
})(typeof globalThis !== 'undefined' ? globalThis : this);
