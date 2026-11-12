using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using TrafficSpeedAnalyzer.App.ViewModels;
using TrafficSpeedAnalyzer.Core.Localization;

namespace TrafficSpeedAnalyzer.App;

public partial class MainWindow
{
    private readonly MainViewModel _vm = new(new Services.WpfPlatformServices());
    private string? _pendingOpen;
    private bool _closingConfirmed;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;

        // Numeri e date nei binding con la cultura della lingua scelta.
        ApplyLanguage();
        Loc.Changed += ApplyLanguage;
        Loaded += OnLoaded;
        Closing += OnClosing;
        PreviewKeyDown += OnPreviewKeyDown;
        _vm.PropertyChanged += OnViewModelPropertyChanged;
        _vm.VideoCaptionRequested += OnVideoCaptionRequested;
        DragOver += OnDragOver;
        Drop += OnDrop;
    }

    private void OnSettingsClick(object sender, RoutedEventArgs e) =>
        new SettingsWindow { Owner = this, DataContext = _vm }.ShowDialog();

    /// <summary>«Genera video»: prima indirizzo e data da scrivere nel video, poi la scelta del file.</summary>
    private async void OnVideoCaptionRequested(object? sender, EventArgs e)
    {
        if (new VideoCaptionWindow { Owner = this, DataContext = _vm }.ShowDialog() == true)
        {
            await _vm.ExportVideoCommand.ExecuteAsync(null);
        }
    }

    /// <summary>Pulsanti in fondo ai risultati: filtro, limite o statistiche in una finestra (il Tag dice quale).</summary>
    private void OnResultsPanelClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string name } && Enum.TryParse(name, out ResultsPanel panel))
        {
            new ResultsPanelWindow(_vm, panel) { Owner = this }.ShowDialog();
        }
    }

    private void ApplyLanguage() =>
        Language = System.Windows.Markup.XmlLanguage.GetLanguage(Loc.Culture.IetfLanguageTag);

    /// <summary>Video da aprire appena FFmpeg è pronto (argomento da riga di comando).</summary>
    public void OpenOnStartup(string path) => _pendingOpen = path;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await _vm.InitializeCommand.ExecuteAsync(null);
        if (_pendingOpen is { } path && File.Exists(path))
        {
            _pendingOpen = null;
            await _vm.OpenVideoFileAsync(path);
        }
    }

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_closingConfirmed)
        {
            return;
        }

        if (_vm.HasUnsavedMeasurements)
        {
            MessageBoxResult r = System.Windows.MessageBox.Show(
                Loc.T("Ci sono misure non esportate che andranno perse. Chiudere comunque?"),
                AppInfo.Name,
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (r != MessageBoxResult.Yes)
            {
                e.Cancel = true;
                return;
            }
        }

        // Chiude i processi ffmpeg prima di uscire.
        e.Cancel = true;
        _closingConfirmed = true;
        await _vm.DisposeAsync();

        // Close() non può essere chiamato dentro l'evento Closing: lo si rimanda al ciclo successivo.
        _ = Dispatcher.BeginInvoke(new Action(Close));
    }

    private static string? DroppedVideo(DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop) || e.Data.GetData(DataFormats.FileDrop) is not string[] files)
        {
            return null;
        }

        return files.FirstOrDefault(f => MainViewModel.VideoExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
               ?? files.FirstOrDefault();
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = DroppedVideo(e) is not null && _vm.IsReady ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnDrop(object sender, DragEventArgs e)
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
            _ = Dispatcher.BeginInvoke(new Action(() => DistanceBox.Focus()), System.Windows.Threading.DispatcherPriority.Input);
        }
    }

    /// <summary>Scorciatoie da tastiera, ignorate mentre si scrive in una casella di testo.</summary>
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is TextBoxBase or PasswordBox or ComboBox)
        {
            if (e.Key == Key.Escape)
            {
                Keyboard.ClearFocus();
                Viewport.Focus();
                e.Handled = true;
            }
            else if (e.Key == Key.Enter && ReferenceEquals(Keyboard.FocusedElement, DistanceBox) && _vm.IsCalibrationValid)
            {
                // Invio dopo la distanza = «Procedi».
                Run(_vm.GoToStepCommand, "Results");
                e.Handled = true;
            }

            return;
        }

        bool shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);

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
            case Key.Z when ctrl:
                Run(_vm.UndoPointCommand);
                break;
            case Key.O when ctrl:
                Run(_vm.OpenVideoCommand);
                break;
            case Key.Enter when _vm.SelectedStep == WorkflowStep.Measurement:
                Run(_vm.SavePassageCommand);
                break;
            case Key.D1 or Key.NumPad1 when ctrl:
                _vm.SelectedStep = WorkflowStep.Video;
                break;
            case Key.D2 or Key.NumPad2 when ctrl && _vm.HasVideo:
                _vm.SelectedStep = WorkflowStep.Road;
                break;
            case Key.D3 or Key.NumPad3 when ctrl && _vm.HasVideo:
                _vm.SelectedStep = WorkflowStep.Analysis;
                break;
            case Key.D4 or Key.NumPad4 when ctrl && _vm.HasVideo:
                _vm.SelectedStep = WorkflowStep.Calibration;
                break;
            case Key.D5 or Key.NumPad5 when ctrl && _vm.HasVideo:
                _vm.SelectedStep = WorkflowStep.Measurement;
                break;
            case Key.D6 or Key.NumPad6 when ctrl && _vm.HasVideo:
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
