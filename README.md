# SpeedVision

Applicazione desktop per Windows che misura la velocità media dei veicoli tra due linee
di rilevamento in un video di traffico ripreso da camera fissa. Tutto avviene sul PC:
il video non viene mai inviato in rete.

## Stato (fasi 1–5)

| Funzione | Stato |
|---|---|
| Importazione video (trascinamento o file), qualsiasi formato letto da FFmpeg | ✅ |
| Timestamp reali di ogni fotogramma, anche con frame rate variabile | ✅ |
| Anteprima, riproduzione, avanzamento fotogramma per fotogramma, zoom | ✅ |
| Linee A/B disegnabili e trascinabili, distanza misurata, verifica della calibrazione | ✅ |
| Orario di inizio registrazione dai metadati (modificabile) | ✅ |
| Calcolo della velocità con istante di attraversamento sub-fotogramma | ✅ |
| **Misura manuale**: l'utente indica la posizione del veicolo | ✅ |
| Esportazione: 2 immagini per veicolo con orario sovrimpresso + CSV | ✅ |
| **Analisi automatica**: rilevamento YOLOX (ONNX Runtime) + tracker ByteTrack + passaggi | ✅ |
| Prova delle prestazioni sul PC, zona di analisi attorno alle linee, salto dei tratti vuoti | ✅ |

### Analisi automatica

- Modelli YOLOX di Megvii (Apache-2.0): Preciso (S, consigliato), Bilanciato (Tiny), Veloce (Nano).
  Si scaricano una sola volta da GitHub, con verifica SHA-256, in `%LocalAppData%\TrafficSpeedAnalyzer\models`.
  Si possono anche copiare lì a mano. I video non escono mai dal PC.
- Accelerazione: DirectML (scheda grafica) se dà gli stessi risultati della CPU su un'immagine di prova,
  altrimenti CPU. Su alcuni PC DirectML restituisce uscite non valide senza errori: il controllo lo evita.
- Il tracker segue i veicoli tra i fotogrammi; ogni traccia che attraversa A e B diventa un passaggio,
  con lo stesso calcolo sub-fotogramma della misura manuale. Punto di riferimento: il vertice in basso
  sul davanti del riquadro (il destro per chi va verso destra, il sinistro per chi va verso sinistra),
  che taglia per primo le linee. I riquadri tagliati dal bordo vengono esclusi.
- Ogni passaggio automatico si può rivedere, correggere a mano o eliminare.

## Requisiti

- Windows 10 o 11 (64 bit)
- [SDK .NET 10](https://dotnet.microsoft.com/download) per compilare
- FFmpeg (gratuito): `winget install --id Gyan.FFmpeg -e`
  (in alternativa copia `ffmpeg.exe` e `ffprobe.exe` in una cartella `ffmpeg` accanto all'eseguibile)

Se sul PC è installato solo .NET 8 o 9, cambia `TsaTargetFramework` in `Directory.Build.props`.

## Compilare, testare, avviare

```powershell
dotnet build SpeedVision.sln -c Release
dotnet test SpeedVision.sln -c Release
dotnet run --project src/TrafficSpeedAnalyzer.App -c Release
```

Oppure `scripts\build-and-test.ps1`. Per un eseguibile autonomo: `scripts\publish.ps1`.

I test video generano piccoli filmati con FFmpeg e verificano che ogni fotogramma letto sia
esattamente quello richiesto; se FFmpeg non è installato vengono **saltati** (non superati).

## Struttura

```
src/
  TrafficSpeedAnalyzer.Core    calcoli puri: geometria, calibrazione, attraversamenti, velocità, CSV
  TrafficSpeedAnalyzer.Video   FFmpeg/FFprobe: metadati, indice dei fotogrammi, lettura esatta
  TrafficSpeedAnalyzer.App     interfaccia WPF (Fluent, tema scuro), MVVM
  TrafficSpeedAnalyzer.Detection  YOLOX con ONNX Runtime, gestione modelli, pipeline di analisi
tests/
  TrafficSpeedAnalyzer.Core.Tests       traiettorie sintetiche a velocità nota, tracker
  TrafficSpeedAnalyzer.Video.Tests      parsing + video generati con contenuto noto
  TrafficSpeedAnalyzer.Detection.Tests  modello reale su video con auto vere a 50 e 72 km/h
```

## Come viene calcolata la velocità

1. Ogni fotogramma ha il suo timestamp reale letto dal file (non `indice / fps`).
2. Per ciascuna linea si trova la coppia di fotogrammi a cavallo dell'attraversamento.
3. Dalla distanza in pixel del punto di riferimento dalla linea, prima e dopo, si ricava
   la frazione di intervallo in cui è avvenuto l'attraversamento (interpolazione); con più
   punti attorno alla linea si usa una retta di regressione, che riduce il rumore dei clic.
4. Se il veicolo compare già oltre la prima linea, l'istante viene ricostruito all'indietro
   solo se mancano al massimo 120 ms; altrimenti la misura è scartata.
5. Velocità = distanza misurata / (t uscita − t ingresso). L'incertezza indicata combina
   quelle dei due istanti.

Le distanze in pixel servono solo a interpolare nel tempo: la scala metrica viene sempre
dalla distanza misurata sul campo, quindi la prospettiva non introduce errori in questo
passaggio. Restano da considerare la posizione della camera (meglio perpendicolare alla
strada) e la cura nel cliccare sempre lo stesso punto del veicolo.

## Esportazione

Per ogni esportazione viene creata una cartella `<video>_misure_<data>` con:

- `immagini/0001_ingresso.jpg`, `immagini/0001_uscita.jpg`, … — fotogramma a piena risoluzione, linee,
  punto del veicolo, orario stimato di attraversamento in grande e orario del fotogramma in piccolo;
- `immagini_anonimizzate/` — le stesse immagini con i veicoli tracciati pixellati;
- `passaggi.csv` — UTF-8 con BOM; separatore e decimali secondo la lingua scelta. Colonne:
  `Id;Verso;[Corsia;]Orario ingresso;Orario uscita;Tempo impiegato (s);Velocità (km/h)`;
- `rapporto.pdf` — riepilogo con statistiche e grafico, poi una pagina per veicolo con i dati e le
  immagini di ingresso e di uscita pixellate;
- `calibrazione.tsacal.json` e `info.txt`.

I nomi di file e cartelle seguono la lingua dell'interfaccia (italiano, inglese, francese, tedesco, spagnolo).

## Avvertenze

Le misure non sono omologate e non sono utilizzabili per sanzioni. Le immagini possono
contenere targhe e persone: sono dati personali (GDPR), conservale e condividile con cautela.

Registro degli errori: `%LocalAppData%\TrafficSpeedAnalyzer\logs\app.log`.

## Licenza

SpeedVision è software libero con licenza [MIT](LICENSE). I componenti di terze parti e le loro
licenze sono elencati in [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
