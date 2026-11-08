#!/usr/bin/env bash
# Compila FFmpeg per SpeedVision su Mac (chip Apple): ffmpeg e ffprobe autonomi, con libx264
# (per i video di prova e le copie stabilizzate) e vidstab (per la stabilizzazione).
# Le build pronte per Mac che si trovano in rete non includono vidstab, per questo lo compiliamo noi.
#
# Ogni sorgente è fissato a un commit preciso: dopo lo scaricamento si controlla che il commit sia
# quello atteso, così una build si può rifare identica. Gli eseguibili dipendono solo dalle
# librerie di sistema (controllato alla fine con otool / ldd).
#
# Uso:  scripts/build-ffmpeg-macos.sh [cartella di uscita]      (predefinita: out/ffmpeg-macos)
# Serve: Xcode Command Line Tools, cmake, pkg-config (su GitHub Actions ci sono già).
# Funziona anche su Linux, utile per provare lo script senza un Mac.
#
# Licenza: con x264 FFmpeg è GPL (come la build di Gyan usata su Windows).
set -euo pipefail

# --- Versioni: per aggiornarle cambia insieme etichetta e commit ---------------------------------
FFMPEG_TAG='n9.0.2'
FFMPEG_COMMIT='946fcce07b6dcd0331c8cc609192aeff5e1924f8'
X264_COMMIT='31e19f92f00c7003fa115047ce50978bc98c3a0d'   # ramo «stable» di x264
VIDSTAB_TAG='v1.1.1'
VIDSTAB_COMMIT='90c76aca2cb06c3ff6f7476a7cd6851b39436656'
# -----------------------------------------------------------------------------------------------

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
OUT="${1:-$ROOT/out/ffmpeg-macos}"
WORK="${FFMPEG_WORK_DIR:-$ROOT/out/ffmpeg-build}"
PREFIX="$WORK/prefix"
JOBS="$( (sysctl -n hw.ncpu 2>/dev/null || nproc 2>/dev/null || echo 4) | head -n1)"

mkdir -p "$WORK/src" "$PREFIX" "$OUT"
OUT="$(cd "$OUT" && pwd)"
export PKG_CONFIG_PATH="$PREFIX/lib/pkgconfig"
case "$(uname -s)" in
    Darwin) export MACOSX_DEPLOYMENT_TARGET="${MACOSX_DEPLOYMENT_TARGET:-12.0}" ;;
esac

# Scarica (se manca) il sorgente a un commit preciso e controlla che sia proprio quello.
fetch() {
    local name="$1" commit="$3"
    shift 3
    local dir="$WORK/src/$name"
    if [ ! -d "$dir/.git" ]; then
        local url
        for url in "$@"; do
            rm -rf "$dir"
            git init -q "$dir"
            if git -C "$dir" fetch -q --depth 1 "$url" "$commit" 2>/dev/null; then
                git -C "$dir" -c advice.detachedHead=false checkout -q FETCH_HEAD
                break
            fi
            echo "  $url non raggiungibile, provo il successivo" >&2
        done
    fi
    local actual
    actual="$(git -C "$dir" rev-parse HEAD 2>/dev/null || true)"
    if [ "$actual" != "$commit" ]; then
        echo "ERRORE: $name è al commit '$actual', atteso $commit" >&2
        exit 1
    fi
    echo "$name: $commit"
}

echo "== Sorgenti"
fetch ffmpeg "$FFMPEG_TAG" "$FFMPEG_COMMIT" https://github.com/FFmpeg/FFmpeg.git https://git.ffmpeg.org/ffmpeg.git
fetch x264 stable "$X264_COMMIT" https://code.videolan.org/videolan/x264.git https://github.com/mirror/x264.git
fetch vidstab "$VIDSTAB_TAG" "$VIDSTAB_COMMIT" https://github.com/georgmartius/vid.stab.git

echo "== x264"
if [ ! -f "$PREFIX/lib/libx264.a" ]; then
    (cd "$WORK/src/x264" && ./configure --prefix="$PREFIX" --enable-static --disable-cli --enable-pic \
        && make -j"$JOBS" && make install)
fi

echo "== vid.stab"
if [ ! -f "$PREFIX/lib/libvidstab.a" ]; then
    # Senza OpenMP: su Mac il compilatore di Apple non lo ha, e così la build è uguale ovunque.
    cmake -S "$WORK/src/vidstab" -B "$WORK/vidstab-build" -DCMAKE_BUILD_TYPE=Release \
        -DCMAKE_INSTALL_PREFIX="$PREFIX" -DCMAKE_INSTALL_LIBDIR=lib -DBUILD_SHARED_LIBS=OFF -DUSE_OMP=OFF \
        -DCMAKE_POSITION_INDEPENDENT_CODE=ON
    cmake --build "$WORK/vidstab-build" -j "$JOBS"
    cmake --install "$WORK/vidstab-build"
fi

echo "== FFmpeg"
# --disable-autodetect: niente librerie trovate per caso sulla macchina di build (Homebrew ecc.),
# così ffmpeg gira su qualunque Mac. Rete disattivata: SpeedVision legge solo file locali.
# Senza la cronologia git FFmpeg mostrerebbe solo il commit: il file VERSION gli dà il numero giusto.
echo "${FFMPEG_TAG#n}" > "$WORK/src/ffmpeg/VERSION"
(cd "$WORK/src/ffmpeg" && ./configure --prefix="$PREFIX" \
    --pkg-config-flags=--static \
    --extra-cflags="-I$PREFIX/include" --extra-ldflags="-L$PREFIX/lib" \
    --enable-gpl --enable-libx264 --enable-libvidstab \
    --disable-autodetect --disable-network --disable-ffplay --disable-doc --disable-debug \
    --enable-static --disable-shared \
    && make -j"$JOBS")

cp "$WORK/src/ffmpeg/ffmpeg" "$WORK/src/ffmpeg/ffprobe" "$OUT/"
strip "$OUT/ffmpeg" "$OUT/ffprobe" 2>/dev/null || true
cp "$WORK/src/ffmpeg/COPYING.GPLv3" "$OUT/LICENSE.txt"
{
    echo "FFmpeg $FFMPEG_TAG ($FFMPEG_COMMIT), compilato per SpeedVision con scripts/build-ffmpeg-macos.sh"
    echo "  x264 $X264_COMMIT — https://code.videolan.org/videolan/x264"
    echo "  vid.stab $VIDSTAB_TAG ($VIDSTAB_COMMIT) — https://github.com/georgmartius/vid.stab"
    echo "Codice sorgente: https://github.com/FFmpeg/FFmpeg/tree/$FFMPEG_COMMIT"
    echo "Licenza: GPL versione 3 o successiva (LICENSE.txt)."
} > "$OUT/README.txt"

echo "== Controlli"
version="$("$OUT/ffmpeg" -hide_banner -version)"
echo "${version%%$'\n'*}"
# (letto in variabili: con pipefail, «grep -q» in una pipe farebbe fallire ffmpeg per SIGPIPE)
encoders="$("$OUT/ffmpeg" -hide_banner -encoders 2>/dev/null)"
filters="$("$OUT/ffmpeg" -hide_banner -filters 2>/dev/null)"
for need in libx264; do
    grep -q "$need" <<<"$encoders" || { echo "ERRORE: manca $need" >&2; exit 1; }
done
for need in vidstabdetect vidstabtransform; do
    grep -q "$need" <<<"$filters" || { echo "ERRORE: manca il filtro $need" >&2; exit 1; }
done
# Solo librerie di sistema: nessun percorso di Homebrew o della cartella di build.
if command -v otool >/dev/null; then deps="$(otool -L "$OUT/ffmpeg" "$OUT/ffprobe")"; else deps="$(ldd "$OUT/ffmpeg" "$OUT/ffprobe")"; fi
echo "$deps"
if echo "$deps" | grep -E "/opt/homebrew|/usr/local|$WORK" >/dev/null; then
    echo "ERRORE: ffmpeg dipende da librerie fuori dal sistema" >&2
    exit 1
fi
echo "FFmpeg pronto in $OUT"
