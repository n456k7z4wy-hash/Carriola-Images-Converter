using System;
using System.ComponentModel;
using System.IO;
using Microsoft.UI.Xaml.Media;

namespace CarriolaConverter;

public sealed partial class QueueItem : INotifyPropertyChanged
{
    public Guid Id { get; } = Guid.NewGuid();
    public string Source { get; }
    public string Name => Path.GetFileName(Source);
    public int Sequence { get; }
    public bool IsTemporary { get; }
    public JobState State { get; private set; } = JobState.Queued;
    public double Percent { get; private set; }
    public string PercentText => $"{Percent:0}%";
    public string Message { get; private set; } = "Na fila";
    public string? OutputPath { get; private set; }
    public long? SourceBytes { get; private set; }
    public long? OutputBytes { get; private set; }
    public string SizeText => OutputBytes is null
        ? FileSizes.Format(SourceBytes)
        : $"{FileSizes.Format(SourceBytes)} → {FileSizes.Format(OutputBytes)}";
    public string DifferenceText => FileSizes.Difference(SourceBytes, OutputBytes);
    public string DimensionsText { get; private set; } = "";
    public ImageSource? Thumbnail { get; private set; }
    public double PlaceholderOpacity => Thumbnail is null ? 0.5 : 0;
    public bool ThumbnailAttempted { get; set; }
    public bool CanRemove { get; private set; } = true;
    public bool CanPreview { get; private set; } = true;
    public event PropertyChangedEventHandler? PropertyChanged;

    public QueueItem(string source, int sequence = 0, long? size = null, bool temporary = false)
    { Source = source; Sequence = sequence; SourceBytes = size; IsTemporary = temporary; }

    public void Apply(ConversionUpdate update)
    {
        State = update.State;
        Percent = Math.Clamp(update.Percent, 0, 100);
        Message = update.Message;
        OutputPath = update.OutputPath;
        SourceBytes = update.SourceBytes ?? SourceBytes;
        OutputBytes = update.OutputBytes;
        Notify();
    }

    public void SetThumbnail(ImageSource? source, ImageDimensions? dimensions = null)
    {
        Thumbnail = source;
        if (dimensions is not null) DimensionsText = $"{dimensions.Width} × {dimensions.Height}";
        Notify();
    }

    public void SetInteraction(bool enabled)
    {
        if (CanRemove == enabled && CanPreview == enabled) return;
        CanRemove = enabled;
        CanPreview = enabled;
        Notify();
    }

    private void Notify() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
}
