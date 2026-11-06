using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace TrafficSpeedAnalyzer.App.ViewModels;

/// <summary>
/// Guida del passo Video: subito dopo l'apertura si chiede se stabilizzare (riquadro con la cornice
/// verde); poi la data di inizio registrazione, con la possibilità di proseguire senza; infine «Procedi».
/// </summary>
public sealed partial class MainViewModel
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsStabilizationStepActive), nameof(ShowRecordingStartCard), nameof(IsRecordingStepActive), nameof(ShowVideoProceed))]
    private bool _stabilizationAnswered;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRecordingStepActive), nameof(ShowVideoProceed))]
    private bool _recordingStartAnswered;

    /// <summary>Analisi completata sul video in uso: i pulsanti di avvio non sono più l'azione principale.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowAnalysisProceed))]
    private bool _hasCompletedAnalysis;

    /// <summary>Primo passo dopo l'apertura: stabilizzare o no.</summary>
    public bool IsStabilizationStepActive => HasVideo && !StabilizationAnswered;

    /// <summary>Il riquadro della data compare dopo la risposta sulla stabilizzazione.</summary>
    public bool ShowRecordingStartCard => HasVideo && StabilizationAnswered;

    public bool IsRecordingStepActive => ShowRecordingStartCard && !RecordingStartAnswered;

    /// <summary>«Procedi» compare quando entrambe le domande hanno una risposta.</summary>
    public bool ShowVideoProceed => HasVideo && StabilizationAnswered && RecordingStartAnswered;

    /// <summary>Le domande della guida ripartono a ogni nuovo video.</summary>
    private void ResetVideoGuide()
    {
        StabilizationAnswered = false;
        RecordingStartAnswered = false;
        HasCompletedAnalysis = false;
        OnPropertyChanged(nameof(IsStabilizationStepActive));
        OnPropertyChanged(nameof(ShowRecordingStartCard));
        OnPropertyChanged(nameof(IsRecordingStepActive));
        OnPropertyChanged(nameof(ShowVideoProceed));
    }

    [RelayCommand]
    private void SkipStabilization() => StabilizationAnswered = true;

    private bool CanConfirmRecordingStart() => EffectiveRecordingStart is not null;

    [RelayCommand(CanExecute = nameof(CanConfirmRecordingStart))]
    private void ConfirmRecordingStart() => RecordingStartAnswered = true;

    [RelayCommand]
    private void SkipRecordingStart()
    {
        RecordingStartText = string.Empty;
        RecordingStartAnswered = true;
    }
}
