using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace CarriolaConverter;

public sealed partial class PreviewDialog : ContentDialog
{
    private readonly string _source;
    private readonly ConversionOptions _options;
    private readonly string _cache;
    private readonly CancellationTokenSource _cancellation = new();
    private Task _work = Task.CompletedTask;
    private bool _ending;
    private bool _loaded;
    private bool _syncing;

    public PreviewDialog(string source, ConversionOptions options, string cache)
    {
        InitializeComponent();
        _source = source; _options = options; _cache = cache;
        FileNameText.Text = System.IO.Path.GetFileName(source);
        AfterTitle.Text = "Convertida · " + options.Output.Name;
    }

    public void RequestClose() { _ending = true; _cancellation.Cancel(); Hide(); }

    public async Task ShowPreviewAsync()
    {
        try { await ShowAsync(); }
        finally
        {
            _ending = true;
            _cancellation.Cancel();
            await _work;
            _cancellation.Dispose();
            BeforeImage.Source = null;
            AfterImage.Source = null;
        }
    }

    private void Dialog_Opened(ContentDialog sender, ContentDialogOpenedEventArgs args)
    {
        double width = Math.Max(280, Math.Min(920, XamlRoot.Size.Width - 100));
        Comparison.Width = width;
        BeforeImage.Width = AfterImage.Width = Math.Max(120, (width - 48) / 2);
        _work = LoadAsync();
    }

    private void Dialog_Closing(ContentDialog sender, ContentDialogClosingEventArgs args)
    { _ending = true; _cancellation.Cancel(); }

    private async Task LoadAsync()
    {
        try
        {
            var brush = await Checkerboard.CreateBrushAsync();
            if (_ending) return;
            BeforeViewer.Background = AfterViewer.Background = brush;
            var result = await ImagePipeline.PreviewAsync(_source, _options, _cache, _cancellation.Token);
            if (_ending) return;
            var before = await BitmapLoader.FromPngAsync(result.BeforePng);
            var after = await BitmapLoader.FromPngAsync(result.AfterPng);
            if (_ending) return;
            BeforeImage.Source = before;
            AfterImage.Source = after;
            BeforeInfo.Text = $"{result.Before.Width} × {result.Before.Height} pixels · {FileSizes.Format(result.SourceBytes)}";
            AfterInfo.Text = $"{result.After.Width} × {result.After.Height} pixels · {FileSizes.Format(result.OutputBytes)}";
            SavingsText.Text = "Arquivo convertido: " + FileSizes.Difference(result.SourceBytes, result.OutputBytes) + ".";
            PreviewHint.Text = result.Reduced
                ? "Exibição reduzida para até 2048 pixels por lado. A comparação usa a imagem realmente comprimida, com as configurações atuais."
                : "A comparação usa a imagem realmente comprimida, com as configurações atuais.";
            _loaded = true;
            Comparison.Visibility = ZoomPanel.Visibility = Visibility.Visible;
        }
        catch (Exception) when (_cancellation.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (_ending) return;
            PreviewNotice.Title = "Não foi possível preparar a prévia";
            PreviewNotice.Message = ConversionEngine.DescribeError(ex);
            PreviewNotice.Severity = InfoBarSeverity.Error;
            PreviewNotice.IsOpen = true;
        }
        finally { if (!_ending) LoadingPanel.Visibility = Visibility.Collapsed; }
    }

    private void Zoom_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (!_loaded || _syncing) return;
        _syncing = true;
        try
        {
            ZoomText.Text = $"Zoom da prévia: {e.NewValue:0.##}×";
            BeforeViewer.ChangeView(null, null, (float)e.NewValue, true);
            AfterViewer.ChangeView(null, null, (float)e.NewValue, true);
        }
        finally { _syncing = false; }
    }

    private void Viewer_ViewChanged(object sender, ScrollViewerViewChangedEventArgs e)
    {
        if (!_loaded || _syncing || sender is not ScrollViewer source) return;
        var target = source == BeforeViewer ? AfterViewer : BeforeViewer;
        _syncing = true;
        try
        {
            if (Math.Abs(target.ZoomFactor - source.ZoomFactor) > 0.01)
                target.ChangeView(null, null, source.ZoomFactor, true);
            double x = source.ScrollableWidth > 0 ? source.HorizontalOffset / source.ScrollableWidth * target.ScrollableWidth : 0;
            double y = source.ScrollableHeight > 0 ? source.VerticalOffset / source.ScrollableHeight * target.ScrollableHeight : 0;
            if (Math.Abs(target.HorizontalOffset - x) > 1 || Math.Abs(target.VerticalOffset - y) > 1)
                target.ChangeView(x, y, null, true);
            ZoomSlider.Value = source.ZoomFactor;
            ZoomText.Text = $"Zoom da prévia: {source.ZoomFactor:0.##}×";
        }
        finally { _syncing = false; }
    }
}
