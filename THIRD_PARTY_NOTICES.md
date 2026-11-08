# Componenti di terze parti

SpeedVision è distribuito con licenza MIT (vedi `LICENSE`). Usa questi componenti, ciascuno con la propria licenza:

| Componente | Uso | Licenza |
|---|---|---|
| [YOLOX](https://github.com/Megvii-BaseDetection/YOLOX) (Megvii) | Modelli di rilevamento dei veicoli, scaricati al primo uso | Apache-2.0 |
| [ONNX Runtime](https://github.com/microsoft/onnxruntime) | Esecuzione dei modelli (DirectML su Windows, CoreML su Mac) | MIT |
| [WPF-UI](https://github.com/lepoco/wpfui) | Interfaccia | MIT |
| [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) | Struttura dell'interfaccia | MIT |
| [SkiaSharp](https://github.com/mono/SkiaSharp) e [Skia](https://skia.org) | Disegno delle immagini esportate e dell'anteprima per la mappa | MIT (SkiaSharp), BSD-3-Clause (Skia) |
| [Microsoft Edge WebView2](https://www.nuget.org/packages/Microsoft.Web.WebView2) | Pagina della mappa | Licenza Microsoft del pacchetto |
| [Leaflet](https://leafletjs.com) 1.9.4 | Mappa per la calibrazione (`src/TrafficSpeedAnalyzer.App/Map/leaflet`, con il suo `LICENSE`) | BSD-2-Clause |
| [FFmpeg](https://ffmpeg.org) | Lettura e stabilizzazione dei video. Lo zip della release include `ffmpeg.exe` e `ffprobe.exe` originali (build «essentials» di [Gyan Doshi](https://www.gyan.dev/ffmpeg/builds/)) nella cartella `ffmpeg`, con licenza, versione e link al codice sorgente; SpeedVision li avvia come programmi separati | GPLv3 |

Le mappe e la ricerca dei luoghi usano servizi in rete (OpenStreetMap, immagini satellitari Esri), con i loro termini d'uso; i video non lasciano mai il PC.
