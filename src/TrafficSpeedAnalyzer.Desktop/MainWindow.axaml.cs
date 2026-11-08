using System.ComponentModel;
using System.Windows.Input;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using TrafficSpeedAnalyzer.App;
using TrafficSpeedAnalyzer.App.Services;
using TrafficSpeedAnalyzer.App.ViewModels;
using TrafficSpeedAnalyzer.Core.Localization;
using TrafficSpeedAnalyzer.Desktop.Controls;
using TrafficSpeedAnalyzer.Desktop.Services;

namespace TrafficSpeedAnalyzer.Desktop;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private string? _pendingOpen;
    private bool _closingConfirmed;

    public MainWindow()
        : this(new AvaloniaPlatformServices())
    {
    }

    /// <summary>Con servizi di sistema dati dall'esterno (test senza schermo).</summary>
    public MainWindow(IPlatformServices platform)
    {
        _vm = new MainViewModel(platform);
        if (platform is AvaloniaPlatformServices aps)
        {
            aps.Owner = this;
        }

        InitializeComponent();
        DataContext = _vm;

        Opened += OnOpened;
        Closing += OnClosing;
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
        _vm.PropertyChanged += OnViewModelPropertyChanged;
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
    }

    public MainViewModel ViewModel => _vm;

    /// <summary>Video da aprire appena FFmpeg è pronto (argomento da riga di comando).</summary>
    public void OpenOnStartup(string path) => _pendingOpen = path;

    /// <summary>Messaggio d'errore con il solo pulsante «OK».</summary>
    public Task ShowErrorAsync(string message) => MessageDialog.InformAsync(this, message);

    private void OnSettingsClick(object? sender, RoutedEventArgs e) =>
        _ = new SettingsWindow { DataContext = _vm }.ShowDialog(this);

    private void OnCloseStatusClick(object? sender, RoutedEventArgs e) => _vm.IsStatusOpen = false;

    private async void OnOpened(object? sender, EventArgs e)
    {
        await _vm.InitializeCommand.ExecuteAsync(null);
        if (_pendingOpen is { } path && File.Exists(path))
        {
            _pendingOpen = null;
            await _vm.OpenVideoFileAsync(path);
        }
    }

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_closingConfirmed)
        {
            return;
        }

        // La risposta arriva dopo: intanto si annulla la chiusura e la si ripete dopo.
        e.Cancel = true;
        if (_vm.HasUnsavedMeasurements &&
            await MessageDialog.AskAsync(this, Loc.T("Ci sono misure non esportate che andranno perse. Chiudere comunque?"), DialogKind.Warning, allowCancel: false) != DialogAnswer.Yes)
        {
            return;
        }

        // Chiude i processi ffmpeg prima di uscire.
        _closingConfirmed = true;
        await _vm.DisposeAsync();
        Dispatcher.UIThread.Post(Close);
    }

    private static string? DroppedVideo(DragEventArgs e)
    {
        IStorageItem[] items = [.. e.DataTransfer.TryGetFiles() ?? []];
        string[] files = [.. items.Select(i => i.TryGetLocalPath()).OfType<string>()];
        return files.FirstOrDefault(f => MainViewModel.VideoExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
               ?? files.FirstOrDefault();
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = DroppedVideo(e) is not null && _vm.IsReady ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        if (DroppedVideo(e) is { } path)
        {
            e.Handled = true;
            await _vm.OpenVideoFileAsync(path);
        }
    }

    /// <summary>
    /// Guida: quando le due linee sono pronte il cursore va direttamente nella casella della distanza,
    /// così l'utente può scrivere subito senza cercarla.
    /// </summary>
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.IsDistanceStepActive) && _vm.IsDistanceStepActive
            && _vm.SelectedStep == WorkflowStep.Calibration)
        {
            Dispatcher.UIThread.Post(() => DistanceBox.Focus(), DispatcherPriority.Input);
        }
    }

    /// <summary>Scorciatoie da tastiera, ignorate mentre si scrive in una casella di testo. Su Mac ⌘ al posto di Ctrl.</summary>
    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        IInputElement? focused = FocusManager?.GetFocusedElement();
        if (focused is TextBox or ComboBox)
        {
            if (e.Key == Key.Escape)
            {
                Viewport.Focus();
                e.Handled = true;
            }
            else if (e.Key == Key.Enter && ReferenceEquals(focused, DistanceBox) && _vm.IsCalibrationValid)
            {
                // Invio dopo la distanza = «Procedi».
                Run(_vm.GoToStepCommand, "Results");
                e.Handled = true;
            }

            return;
        }

        bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        bool command = e.KeyModifiers.HasFlag(OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control);

        switch (e.Key)
        {
            case Key.Left:
                Run(_vm.StepFramesCommand, shift ? "-10" : "-1");
                break;
            case Key.Right:
                Run(_vm.StepFramesCommand, shift ? "10" : "1");
                break;
            case Key.PageUp:
                Run(_vm.StepFramesCommand, "-100");
                break;
            case Key.PageDown:
                Run(_vm.StepFramesCommand, "100");
                break;
            case Key.Home:
                Run(_vm.GoToStartCommand);
                break;
            case Key.End:
                Run(_vm.GoToEndCommand);
                break;
            case Key.Space:
                Run(_vm.TogglePlayCommand);
                break;
            case Key.Escape:
                Run(_vm.CancelDrawingCommand);
                Viewport.ResetZoom();
                break;
            case Key.Z when command:
                Run(_vm.UndoPointCommand);
                break;
            case Key.O when command:
                Run(_vm.OpenVideoCommand);
                break;
            case Key.Enter when _vm.SelectedStep == WorkflowStep.Measurement:
                Run(_vm.SavePassageCommand);
                break;
            case Key.D1 or Key.NumPad1 when command:
                _vm.SelectedStep = WorkflowStep.Video;
                break;
            case Key.D2 or Key.NumPad2 when command && _vm.HasVideo:
                _vm.SelectedStep = WorkflowStep.Road;
                break;
            case Key.D3 or Key.NumPad3 when command && _vm.HasVideo:
                _vm.SelectedStep = WorkflowStep.Analysis;
                break;
            case Key.D4 or Key.NumPad4 when command && _vm.HasVideo:
                _vm.SelectedStep = WorkflowStep.Calibration;
                break;
            case Key.D5 or Key.NumPad5 when command && _vm.HasVideo:
                _vm.SelectedStep = WorkflowStep.Measurement;
                break;
            case Key.D6 or Key.NumPad6 when command && _vm.HasVideo:
                _vm.SelectedStep = WorkflowStep.Results;
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    private static void Run(ICommand command, object? parameter = null)
    {
        if (command.CanExecute(parameter))
        {
            command.Execute(parameter);
        }
    }
}
