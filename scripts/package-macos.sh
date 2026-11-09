#!/usr/bin/env bash
# Prepara SpeedVision per Mac con chip Apple: SpeedVision.app (con .NET e FFmpeg dentro) in un file .dmg.
# Chi lo scarica apre il .dmg e trascina SpeedVision nella cartella Applicazioni: non serve installare altro.
#
# Uso (su un Mac):  scripts/package-macos.sh <versione> [cartella FFmpeg] [cartella di uscita]
#   versione        per esempio 0.2.0
#   cartella FFmpeg con ffmpeg, ffprobe e LICENSE.txt di scripts/build-ffmpeg-macos.sh (predefinita: out/ffmpeg-macos)
#   uscita          predefinita: out
#
# Firma: per ora «ad hoc» (nessun certificato), obbligatoria sui Mac con chip Apple perché il programma parta.
# Al primo avvio macOS avvisa che lo sviluppatore non è verificato: va aperto da Impostazioni di sistema →
# Privacy e sicurezza → «Apri comunque». Con un account Apple Developer si firmerà e notarizzerà qui.
set -euo pipefail

VERSION="${1:?Indica la versione, per esempio 0.2.0}"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
FFMPEG_DIR="${2:-$ROOT/out/ffmpeg-macos}"
OUT="${3:-$ROOT/out}"
RID=osx-arm64
NAME=SpeedVision
BUNDLE_ID=io.github.bugeo.speedvision

for f in ffmpeg ffprobe; do
    [ -x "$FFMPEG_DIR/$f" ] || { echo "ERRORE: manca $FFMPEG_DIR/$f (lancia prima scripts/build-ffmpeg-macos.sh)" >&2; exit 1; }
done

mkdir -p "$OUT"
OUT="$(cd "$OUT" && pwd)"
WORK="$OUT/macos-package"
APP="$WORK/dmg/$NAME.app"
rm -rf "$WORK"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"

echo "== Compilazione di $NAME $VERSION per $RID"
dotnet publish "$ROOT/src/TrafficSpeedAnalyzer.Desktop" -c Release -r "$RID" --self-contained true \
    -p:Version="$VERSION" -p:DebugType=none -p:DebugSymbols=false -o "$APP/Contents/MacOS"
# File per sviluppatori che non servono a chi usa il programma
rm -f "$APP/Contents/MacOS/"*.pdb "$APP/Contents/MacOS/createdump"

echo "== FFmpeg nella cartella «ffmpeg» accanto al programma (dove lo cerca per primo)"
mkdir -p "$APP/Contents/MacOS/ffmpeg"
cp "$FFMPEG_DIR/ffmpeg" "$FFMPEG_DIR/ffprobe" "$APP/Contents/MacOS/ffmpeg/"
cp "$FFMPEG_DIR/LICENSE.txt" "$FFMPEG_DIR/README.txt" "$APP/Contents/MacOS/ffmpeg/" 2>/dev/null || true
chmod +x "$APP/Contents/MacOS/ffmpeg/ffmpeg" "$APP/Contents/MacOS/ffmpeg/ffprobe" "$APP/Contents/MacOS/$NAME"

echo "== Licenze"
cp "$ROOT/LICENSE" "$APP/Contents/Resources/LICENSE.txt"
cp "$ROOT/THIRD_PARTY_NOTICES.md" "$APP/Contents/Resources/THIRD_PARTY_NOTICES.md"

echo "== Icona"
ICONSET="$WORK/$NAME.iconset"
mkdir -p "$ICONSET"
SRC="$ROOT/src/TrafficSpeedAnalyzer.Desktop/Assets/app.png"
for s in 16 32 128 256; do
    sips -z "$s" "$s" "$SRC" --out "$ICONSET/icon_${s}x${s}.png" >/dev/null
    d=$((s * 2))
    sips -z "$d" "$d" "$SRC" --out "$ICONSET/icon_${s}x${s}@2x.png" >/dev/null
done
iconutil -c icns "$ICONSET" -o "$APP/Contents/Resources/$NAME.icns"

echo "== Info.plist"
cat > "$APP/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleName</key><string>$NAME</string>
    <key>CFBundleDisplayName</key><string>$NAME</string>
    <key>CFBundleIdentifier</key><string>$BUNDLE_ID</string>
    <key>CFBundleExecutable</key><string>$NAME</string>
    <key>CFBundleIconFile</key><string>$NAME.icns</string>
    <key>CFBundlePackageType</key><string>APPL</string>
    <key>CFBundleShortVersionString</key><string>$VERSION</string>
    <key>CFBundleVersion</key><string>$VERSION</string>
    <key>LSMinimumSystemVersion</key><string>12.0</string>
    <key>LSArchitecturePriority</key><array><string>arm64</string></array>
    <key>LSApplicationCategoryType</key><string>public.app-category.utilities</string>
    <key>NSHighResolutionCapable</key><true/>
    <key>NSHumanReadableCopyright</key><string>Marco Staffoli · licenza MIT</string>
</dict>
</plist>
PLIST
plutil -lint "$APP/Contents/Info.plist"

echo "== Firma ad hoc (senza certificato)"
# Prima ogni eseguibile e libreria, poi il pacchetto intero.
# Ogni file in Contents/MacOS va firmato, anche le .dll di .NET e i file della mappa: macOS li considera parti
# del programma e rifiuta un pacchetto con parti non firmate (per i file che non sono codice la firma va negli
# attributi del file). Il programma principale per ultimo, poi il pacchetto intero.
while IFS= read -r -d '' f; do
    [ "$f" = "$APP/Contents/MacOS/$NAME" ] || codesign --force --sign - --timestamp=none "$f"
done < <(find "$APP/Contents/MacOS" -type f -print0)
codesign --force --sign - --timestamp=none "$APP/Contents/MacOS/$NAME"
codesign --force --sign - --timestamp=none "$APP"
codesign --verify --deep --strict --verbose=2 "$APP"

echo "== Controlli"
# (letto in variabili: con pipefail, «head» o «grep -q» in una pipe farebbero fallire il comando per SIGPIPE)
version="$("$APP/Contents/MacOS/ffmpeg/ffmpeg" -hide_banner -version)"
echo "${version%%$'\n'*}"
kind="$(file -b "$APP/Contents/MacOS/$NAME")"
[[ "$kind" == *arm64* ]] || { echo "ERRORE: il programma non è arm64 ($kind)" >&2; exit 1; }
[ -f "$APP/Contents/MacOS/Map/map.html" ] || { echo "ERRORE: mancano i file della mappa" >&2; exit 1; }

echo "== Immagine disco"
ln -s /Applications "$WORK/dmg/Applications"
DMG="$OUT/$NAME-v$VERSION-mac-arm64.dmg"
rm -f "$DMG"
hdiutil create -volname "$NAME $VERSION" -srcfolder "$WORK/dmg" -fs HFS+ -format UDZO -ov "$DMG"
hdiutil verify "$DMG"
echo "Pronto: $DMG ($(du -h "$DMG" | cut -f1))"
