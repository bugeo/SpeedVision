using System.ComponentModel;

namespace TrafficSpeedAnalyzer.App.ViewModels;

/// <summary>
/// Passi completati nella barra in alto: restano sottolineati in verde, così si vede a che punto si è.
/// </summary>
public sealed partial class MainViewModel
{
    private static readonly HashSet<string> StepProgressTriggers =
    [
        nameof(ShowVideoProceed), nameof(ShowRoadProceed), nameof(ShowAnalysisProceed), nameof(IsAnalyzing),
        nameof(Calibration), nameof(PassageCountText), nameof(HasExportFolder), nameof(HasVideo),
    ];

    private bool _stepProgressHooked;

    public bool IsVideoStepDone => ShowVideoProceed;

    public bool IsRoadStepDone => ShowRoadProceed;

    public bool IsAnalysisStepDone => !IsAnalyzing && HasTrackedVehicles;

    public bool IsCalibrationStepDone => IsCalibrationValid;

    /// <summary>Passo facoltativo: completato quando c'è almeno una misura manuale salvata.</summary>
    public bool IsMeasurementStepDone => _savedTracks.Any(t => t.Source == PassageSource.Manual);

    /// <summary>Risultati esportati e nessuna modifica successiva.</summary>
    public bool IsResultsStepDone => HasExportFolder && !_hasUnexportedChanges && _allRows.Count > 0;

    /// <summary>Si aggancia una volta sola agli aggiornamenti del modello: i passi si ricalcolano da soli.</summary>
    private void HookStepProgress()
    {
        if (_stepProgressHooked)
        {
            return;
        }

        _stepProgressHooked = true;
        PropertyChanged += OnStepProgressSource;
    }

    private void OnStepProgressSource(object? sender, PropertyChangedEventArgs e)
    {
        if (string.IsNullOrEmpty(e.PropertyName) || StepProgressTriggers.Contains(e.PropertyName))
        {
            RefreshStepProgress();
        }
    }

    private void RefreshStepProgress()
    {
        OnPropertyChanged(nameof(IsVideoStepDone));
        OnPropertyChanged(nameof(IsRoadStepDone));
        OnPropertyChanged(nameof(IsAnalysisStepDone));
        OnPropertyChanged(nameof(IsCalibrationStepDone));
        OnPropertyChanged(nameof(IsMeasurementStepDone));
        OnPropertyChanged(nameof(IsResultsStepDone));
    }
}
