@echo off
rem Elimina i file della rete strada e di «Analizza immagine», tolti dal programma. Poi si cancella da solo.
cd /d "%~dp0"
del /q "src\TrafficSpeedAnalyzer.Core\Calibration\ImageLineAnalysis.cs"
del /q "src\TrafficSpeedAnalyzer.Core\Calibration\RoadProposal.cs"
del /q "src\TrafficSpeedAnalyzer.Presentation\ViewModels\MainViewModel.ImageLines.cs"
del /q "src\TrafficSpeedAnalyzer.Presentation\Services\RoadExamples.cs"
del /q "src\TrafficSpeedAnalyzer.Detection\RoadNetwork.cs"
del /q "src\TrafficSpeedAnalyzer.Detection\Models\roadnet.onnx"
del /q "tests\TrafficSpeedAnalyzer.Core.Tests\ImageLineAnalysisTests.cs"
del /q "tests\TrafficSpeedAnalyzer.Core.Tests\RoadProposalTests.cs"
del /q "tests\TrafficSpeedAnalyzer.Detection.Tests\RoadNetworkTests.cs"
del /q "tests\TrafficSpeedAnalyzer.Presentation.Tests\RoadExamplesTests.cs"
del /q "tests\TrafficSpeedAnalyzer.Presentation.Tests\RoadNetworkProposalTests.cs"
del /q "tests\TrafficSpeedAnalyzer.Presentation.Tests\Assets\strada_reale.mp4"
rmdir "tests\TrafficSpeedAnalyzer.Presentation.Tests\Assets" 2>nul
echo Fatto: file eliminati.
pause
(goto) 2>nul & del "%~f0"
