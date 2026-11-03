using System;
using System.Collections.ObjectModel;
using System.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeskPanel.Search.Indexing;

namespace DeskPanel.App.Indexes;

/// <summary>
/// State shown by the modal "update all indexes" window. It knows nothing about indexing itself: the owner
/// (<see cref="IndexManagerViewModel"/>) drives it through <see cref="BeginIndex"/>, <see cref="Report"/> and the
/// Complete/Finish methods, and reads the cancellation token it exposes.
/// </summary>
internal sealed partial class UpdateAllViewModel : ObservableObject
{
    private readonly CancellationTokenSource _cancellation = new();
    private readonly int _total;
    private int _position;

    [ObservableProperty]
    private string _indexName = "Preparando…";

    [ObservableProperty]
    private string _counterText = "";

    [ObservableProperty]
    private string _remainingText = "";

    [ObservableProperty]
    private string _phaseText = "";

    [ObservableProperty]
    private double _currentValue;

    [ObservableProperty]
    private double _currentMaximum = 1;

    [ObservableProperty]
    private double _overallValue;

    [ObservableProperty]
    private double _overallMaximum = 1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ButtonText))]
    private bool _isRunning = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ButtonText))]
    private bool _isCancelling;

    [ObservableProperty]
    private string _summary = "";

    public UpdateAllViewModel(int total)
    {
        _total = total;
        _overallMaximum = Math.Max(1, total);
    }

    public ObservableCollection<string> Results { get; } = new();

    public CancellationToken Token => _cancellation.Token;

    public bool HasErrors { get; private set; }

    public string ButtonText => !IsRunning ? "Cerrar" : IsCancelling ? "Cancelando…" : "Cancelar";

    /// <summary>Raised when the window should close (the user pressed "Cerrar").</summary>
    public event Action? CloseRequested;

    // One button, two meanings: while running it cancels, afterwards it closes. The window therefore cannot be
    // closed with work still in progress, and nothing is left running behind a closed dialog.
    [RelayCommand]
    private void CancelOrClose()
    {
        if (IsRunning)
        {
            RequestCancel();
        }
        else
        {
            CloseRequested?.Invoke();
        }
    }

    public void RequestCancel()
    {
        if (IsRunning && !IsCancelling)
        {
            IsCancelling = true;
            PhaseText = "Cancelando… lo ya procesado se conserva.";
            _cancellation.Cancel();
        }
    }

    public void BeginIndex(int position, string name)
    {
        _position = position;
        IndexName = name;
        CounterText = $"Índice {position} de {_total}";
        RemainingText = _total - position == 0 ? "es el último" : $"quedan {_total - position} después de este";
        PhaseText = "Empezando…";
        CurrentValue = 0;
        CurrentMaximum = 1;
        OverallValue = position - 1;
    }

    public void Report(IndexProgress progress)
    {
        PhaseText = progress.Total > 0
            ? $"{progress.Phase}: {progress.Processed}/{progress.Total}  {progress.CurrentFile}"
            : progress.Phase;
        CurrentMaximum = Math.Max(1, progress.Total);
        CurrentValue = progress.Processed;

        // The overall bar moves smoothly: finished indexes plus the fraction done of the current one.
        OverallValue = (_position - 1) + (progress.Total > 0 ? (double)progress.Processed / progress.Total : 0);
    }

    public void CompleteIndex(string name, string detail, bool failed)
    {
        HasErrors |= failed;
        Results.Add($"{(failed ? "✘" : "✔")} {name}: {detail}");
        OverallValue = _position;
    }

    public void Finish()
    {
        IndexName = HasErrors ? "Terminado con errores" : "Terminado";
        CounterText = $"{_total} índice(s) procesados";
        RemainingText = "";
        PhaseText = "";
        CurrentValue = CurrentMaximum;
        Summary = HasErrors ? "Algún índice ha fallado; el detalle está en la lista y en el registro." : "Todos los índices están al día.";
        IsRunning = false;
    }

    public void FinishCancelled()
    {
        IndexName = "Cancelado";
        CounterText = $"{Results.Count} de {_total} índice(s) completados";
        RemainingText = "";
        PhaseText = "";
        Summary = "Lo ya procesado se conserva; la próxima actualización continúa desde ahí.";
        IsRunning = false;
        IsCancelling = false;
    }
}
