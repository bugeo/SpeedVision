using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using TrafficSpeedAnalyzer.App.Services;

namespace TrafficSpeedAnalyzer.App.ViewModels;

/// <summary>
/// Guida del passo Video: subito dopo l'apertura si chiede se stabilizzare (riquadro con la cornice
/// verde); poi la data di inizio registrazione, con la possibilità di proseguire senza. Confermata la data
/// (o scelto di proseguire senza) si passa direttamente al passo Strada. Tornando al passo Video la data
/// si può cambiare: il riquadro si riaccende appena è diversa da quella confermata.
/// </summary>
public sealed partial class MainViewModel
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsStabilizationStepActive), nameof(ShowRecordingStartCard), nameof(IsRecordingStepActive), nameof(ShowVideoProceed))]
    private bool _stabilizationAnswered;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRecordingStepActive), nameof(ShowVideoProceed))]
    private bool _recordingStartAnswered;

    /// <summary>Data confermata l'ultima volta (vuota = proseguito senza data).</summary>
    private string _confirmedRecordingText = string.Empty;

    /// <summary>Analisi completata sul video in uso: i pulsanti di avvio non sono più l'azione principale.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowAnalysisProceed))]
    private bool _hasCompletedAnalysis;

    /// <summary>Primo passo dopo l'apertura: stabilizzare o no.</summary>
    public bool IsStabilizationStepActive => HasVideo && !StabilizationAnswered;

    /// <summary>Il riquadro della data compare dopo la risposta sulla stabilizzazione.</summary>
    public bool ShowRecordingStartCard => HasVideo && StabilizationAnswered;

    /// <summary>Riquadro della data acceso: non ancora confermata, oppure cambiata dopo la conferma.</summary>
    public bool IsRecordingStepActive => ShowRecordingStartCard && (!RecordingStartAnswered || IsRecordingStartModified);

    /// <summary>La data scritta è diversa da quella confermata: va salvata di nuovo.</summary>
    public bool IsRecordingStartModified => RecordingStartAnswered && NormalizedRecordingText(RecordingStartText) != _confirmedRecordingText;

    /// <summary>Passo Video completato: entrambe le domande hanno una risposta.</summary>
    public bool ShowVideoProceed => HasVideo && StabilizationAnswered && RecordingStartAnswered;

    /// <summary>Testo della data senza la maschera vuota: «__/__/____ __:__:__» vale come nessuna data.</summary>
    private static string NormalizedRecordingText(string? text) => InputMask.HasDigits(text) ? text!.Trim() : string.Empty;

    /// <summary>Le domande della guida ripartono a ogni nuovo video.</summary>
    private void ResetVideoGuide()
    {
        StabilizationAnswered = false;
        RecordingStartAnswered = false;
        _confirmedRecordingText = string.Empty;
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
    private void ConfirmRecordingStart() => AnswerRecordingStart(RecordingStartText);

    [RelayCommand]
    private void SkipRecordingStart()
    {
        RecordingStartText = string.Empty;
        AnswerRecordingStart(string.Empty);
    }

    /// <summary>Risposta data: si salva e si passa subito al passo successivo, senza «Procedi».</summary>
    private void AnswerRecordingStart(string text)
    {
        _confirmedRecordingText = NormalizedRecordingText(text);
        RecordingStartAnswered = true;
        OnPropertyChanged(nameof(IsRecordingStartModified));
        OnPropertyChanged(nameof(IsRecordingStepActive));
        SelectedStep = WorkflowStep.Road;
    }
}
