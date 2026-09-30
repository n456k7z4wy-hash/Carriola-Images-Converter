using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.Windows.Storage.Pickers;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.Storage;
using Windows.UI.ViewManagement;
using Launcher = Windows.System.Launcher;

namespace CarriolaConverter;

public sealed partial class MainWindow : Window
{
    private const int QueueLimit = 10_000;
    private readonly List<QueueItem> _all = new();
    private readonly ObservableCollection<QueueItem> _visible = new();
    private readonly HashSet<Guid> _visibleIds = new();
    private readonly ObservableCollection<NamedProfile> _profiles = new();
    private readonly Dictionary<Guid, QueueItem> _byId = new();
    private readonly HashSet<string> _paths = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<DependencyObject, QueueItem> _realized = new();
    private readonly ThumbnailService _thumbnails = new();
    private readonly ConversionEngine _engine = new();
    private readonly BufferedProgress _progress = new();
    private readonly UISettings _uiSettings = new();
    private readonly string _dataFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CarriolaConverter");
    private readonly PreferencesStore _preferences;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _timer;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _saveTimer;
    private readonly TaskbarProgress _taskbar;
    private IReadOnlyList<ExportFormat> _formats = Array.Empty<ExportFormat>();
    private QueueItem[] _batch = Array.Empty<QueueItem>();
    private SessionCache? _cache;
    private CancellationTokenSource? _batchCancellation;
    private CancellationTokenSource? _interactionCancellation;
    private PreviewDialog? _preview;
    private ContentDialog? _profileDialog;
    private Storyboard? _dropAnimation;
    private Storyboard? _successAnimation;
    private UserOptions _lastGood = new();
    private string _folder = "";
    private string? _lastBatchResult;
    private bool _ready;
    private bool _loading = true;
    private bool _initialized;
    private bool _preferencesLoaded;
    private bool _applying;
    private bool _batchRunning;
    private bool _interacting;
    private bool _closeRequested;
    private bool _finishingClose;
    private bool _allowClose;
    private bool _closed;
    private bool _dropActive;
    private bool? _rowInteraction;
    private int _sequence;
    private int _ticks;
    private bool CanInteract => _ready && !_loading && !_batchRunning && !_interacting && !_closeRequested;

    public MainWindow()
    {
        InitializeComponent();
        // Caminho absoluto: funciona também depois da instalação do pacote.
        try
        {
            var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "CarriolaConverter.ico");
            if (File.Exists(iconPath)) AppWindow.SetIcon(iconPath);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Não foi possível carregar o ícone da janela: {ex.Message}");
        }
        _preferences = new PreferencesStore(_dataFolder);
        QueueList.ItemsSource = _visible;
        ProfilesBox.ItemsSource = _profiles;
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        if (AppWindowTitleBar.IsCustomizationSupported())
        {
            AppWindow.TitleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
            AppWindow.TitleBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
            AppWindow.TitleBar.ButtonForegroundColor = Microsoft.UI.Colors.White;
            AppWindow.TitleBar.ButtonHoverBackgroundColor = Windows.UI.Color.FromArgb(255, 58, 53, 72);
        }
        // Instala antes de App.xaml.cs chamar Activate(), para receber TaskbarButtonCreated.
        _taskbar = new TaskbarProgress(WinRT.Interop.WindowNative.GetWindowHandle(this));
        _timer = DispatcherQueue.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(150);
        _timer.Tick += (_, _) =>
        {
            if (_batchRunning) DrainProgress();
            if (++_ticks % 3 == 0 && CanInteract)
                foreach (var item in _realized.Values.Distinct().ToArray())
                    if (_byId.ContainsKey(item.Id)) _thumbnails.Request(item);
        };
        _saveTimer = DispatcherQueue.CreateTimer();
        _saveTimer.Interval = TimeSpan.FromMilliseconds(600);
        _saveTimer.IsRepeating = false;
        _saveTimer.Tick += async (_, _) => await SavePreferencesAsync();
        AppWindow.Closing += Window_Closing;
        Closed += (_, _) =>
        {
            _closed = true;
            CompletionNotification.Shutdown();
            CancelStartup();
            _startupCancellation.Dispose();
            _timer.Stop();
            _saveTimer.Stop();
        };
        CompletionNotification.Initialize(this);
        _ready = true;
        RefreshControls();
    }

    private async void Root_Loaded(object sender, RoutedEventArgs e)
    {
        if (_initialized) return;
        _initialized = true;
        try
        {
            BeginStartup();
            var display = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
            if (display is not null)
            {
                double scale = Root.XamlRoot.RasterizationScale;
                AppWindow.Resize(new SizeInt32(Math.Min((int)(1200 * scale), (int)(display.WorkArea.Width * 0.94)),
                    Math.Min((int)(880 * scale), (int)(display.WorkArea.Height * 0.94))));
            }
            var formatsTask = Task.Run(ConversionEngine.GetFormats);
            var saved = await _preferences.LoadAsync();
            _cache = await Task.Run(() => new SessionCache(_dataFolder));
            _formats = await formatsTask;
            if (_closed || _closeRequested) return;
            _applying = true;
            FormatBox.ItemsSource = _formats;
            foreach (var profile in (saved.Value.Profiles ?? new()).Where(p => p is not null
                && !string.IsNullOrWhiteSpace(p.Name) && p.Options is not null).Take(64)) _profiles.Add(profile);
            ApplySettings(saved.Value.Last ?? new());
            // Só permite salvar após aplicar os ajustes; fechar durante a abertura
            // não deve substituir as preferências existentes pelos valores iniciais.
            _preferencesLoaded = true;
            _applying = false;
            var brush = await Checkerboard.CreateBrushAsync();
            ((ImageBrush)Root.Resources["ThumbnailBackground"]).ImageSource = brush.ImageSource;
            FormatBox.PlaceholderText = "Escolha um formato";
            if (saved.Warning is not null) ShowNotice("Preferências", saved.Warning, InfoBarSeverity.Warning);
            if (_formats.Count == 0) ShowNotice("Formatos indisponíveis", "Não foi possível inicializar os formatos de saída.", InfoBarSeverity.Error);
        }
        catch (Exception ex) { ShowNotice("Não foi possível iniciar todos os recursos", ex.Message, InfoBarSeverity.Error); }
        finally
        {
            _applying = false;
            await CompleteStartupAsync();
            _loading = false;
            if (!_closed)
            {
                if (!_closeRequested) _timer.Start();
                RefreshControls();
                await FinishCloseIfNeededAsync();
            }
        }
    }

    private async Task InteractAsync(Func<CancellationToken, Task> action)
    {
        if (!CanInteract) return;
        _interacting = true;
        using var cancellation = new CancellationTokenSource();
        _interactionCancellation = cancellation;
        RefreshControls();
        try { await action(cancellation.Token); }
        catch (Exception) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex) { if (!_closeRequested) ShowNotice("Não foi possível concluir a ação", ConversionEngine.DescribeError(ex), InfoBarSeverity.Error); }
        finally
        {
            _interactionCancellation = null;
            _interacting = false;
            RefreshControls();
            await FinishCloseIfNeededAsync();
        }
    }

    private async void Add_Click(object sender, RoutedEventArgs e) => await InteractAsync(async token =>
    {
        var picker = new FileOpenPicker(AppWindow.Id)
        {
            SuggestedStartLocation = PickerLocationId.PicturesLibrary,
            ViewMode = PickerViewMode.Thumbnail, CommitButtonText = "Adicionar imagens"
        };
        var files = await picker.PickMultipleFilesAsync();
        token.ThrowIfCancellationRequested();
        if (files is null || files.Count == 0) return;
        await ImportPathsAsync(files.Select(file => file.Path), token);
    });

    private async void AddFolder_Click(object sender, RoutedEventArgs e) => await InteractAsync(async token =>
    {
        var picker = new FolderPicker(AppWindow.Id) { CommitButtonText = "Adicionar imagens desta pasta" };
        var folder = await picker.PickSingleFolderAsync();
        token.ThrowIfCancellationRequested();
        if (folder is not null) await ImportPathsAsync(new[] { folder.Path }, token);
    });

    private async Task ImportPathsAsync(IEnumerable<string> paths, CancellationToken token)
    {
        StatusText.Text = "Procurando imagens…";
        var result = await FileImportService.ScanAsync(paths, SubfoldersCheck.IsChecked == true,
            _folder, Math.Max(0, QueueLimit - _all.Count), token);
        int added = 0, repeated = 0;
        foreach (var file in result.Files)
        {
            token.ThrowIfCancellationRequested();
            if (AddFile(file)) added++; else repeated++;
            if ((added + repeated) % 40 == 0) { UpdateQueueText(); await Task.Delay(1, token); }
        }
        if (added > 0) ResetBatchDisplay();
        UpdateQueueText();
        string details = $"{added} imagem(ns) adicionada(s).";
        if (repeated > 0) details += $" {repeated} repetida(s) ignorada(s).";
        if (result.Skipped > 0) details += $" {result.Skipped} arquivo(s) de outros tipos ignorado(s).";
        if (result.Inaccessible > 0) details += $" {result.Inaccessible} caminho(s) sem acesso.";
        if (result.ReachedLimit) details += $" O limite desta fila é de {QueueLimit:N0} imagens.";
        if (!_closeRequested) ShowNotice("Fila atualizada", details, result.Inaccessible > 0 || result.ReachedLimit
            ? InfoBarSeverity.Warning : InfoBarSeverity.Informational);
    }

    private bool AddFile(ImportFile file)
    {
        if (_all.Count >= QueueLimit || !_paths.Add(file.Path))
        { if (file.IsTemporary) _cache?.Release(file.Path); return false; }
        var item = new QueueItem(file.Path, ++_sequence, file.Size, file.IsTemporary);
        item.SetInteraction(CanInteract);
        _all.Add(item); _byId.Add(item.Id, item);
        if (MatchesFilter(item)) { _visible.Add(item); _visibleIds.Add(item.Id); }
        return true;
    }

    private async void Paste_Click(object sender, RoutedEventArgs e) => await InteractAsync(PasteAsync);
    private async void Paste_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        var focused = FocusManager.GetFocusedElement(Root.XamlRoot);
        if (focused is TextBox or PasswordBox or RichEditBox || !CanInteract) return;
        args.Handled = true;
        await InteractAsync(PasteAsync);
    }

    private async Task PasteAsync(CancellationToken token)
    {
        var data = Clipboard.GetContent();
        if (data.Contains(StandardDataFormats.StorageItems))
        {
            var files = await data.GetStorageItemsAsync();
            await ImportPathsAsync(files.Select(f => f.Path), token);
        }
        else if (data.Contains(StandardDataFormats.Bitmap))
        {
            if (_cache is null) throw new IOException("O espaço temporário das imagens não está disponível.");
            StatusText.Text = "Preparando imagem copiada…";
            var reference = await data.GetBitmapAsync();
            using var randomAccess = await reference.OpenReadAsync();
            using var input = randomAccess.AsStreamForRead();
            var file = await _cache.ImportBitmapAsync(input, token);
            if (token.IsCancellationRequested) { _cache.Release(file.Path); token.ThrowIfCancellationRequested(); }
            if (AddFile(file)) { ResetBatchDisplay(); ShowNotice("Imagem adicionada", "A imagem copiada já está na fila.", InfoBarSeverity.Success); }
            else ShowNotice("Fila cheia", $"O limite desta fila é de {QueueLimit:N0} imagens.", InfoBarSeverity.Warning);
        }
        else ShowNotice("Nada para colar", "Copie uma imagem ou arquivos no Explorador do Windows e tente novamente.", InfoBarSeverity.Informational);
    }

    private void DropZone_DragOver(object sender, DragEventArgs e)
    {
        bool accept = CanInteract && e.DataView.Contains(StandardDataFormats.StorageItems);
        e.AcceptedOperation = accept ? DataPackageOperation.Copy : DataPackageOperation.None;
        if (accept) e.DragUIOverride.Caption = "Adicionar imagens e pastas à fila";
        AnimateDrop(accept); e.Handled = true;
    }
    private void DropZone_DragLeave(object sender, DragEventArgs e) => AnimateDrop(false);
    private async void DropZone_Drop(object sender, DragEventArgs e)
    {
        AnimateDrop(false);
        if (!CanInteract || !e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        e.Handled = true;
        var deferral = e.GetDeferral();
        try
        {
            await InteractAsync(async token =>
            {
                var entries = await e.DataView.GetStorageItemsAsync();
                await ImportPathsAsync(entries.Select(f => f.Path), token);
            });
        }
        finally { deferral.Complete(); }
    }

    private async void Folder_Click(object sender, RoutedEventArgs e) => await InteractAsync(async token =>
    {
        var picker = new FolderPicker(AppWindow.Id) { CommitButtonText = "Salvar nesta pasta" };
        var folder = await picker.PickSingleFolderAsync();
        token.ThrowIfCancellationRequested();
        if (folder is null) return;
        _folder = folder.Path;
        UpdateFolder();
        SettingsChanged();
    });

    private async void Preview_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: QueueItem item }) return;
        await InteractAsync(async token =>
        {
            if (_cache is null) throw new IOException("O espaço temporário das imagens não está disponível.");
            var settings = CaptureSettings();
            var options = ToConversionOptions(settings);
            StatusText.Text = "Preparando a comparação…";
            _preview = new PreviewDialog(item.Source, options, _cache.Folder) { XamlRoot = Root.XamlRoot };
            _preview.Closing += (_, _) => { if (!_closeRequested) StatusText.Text = "Finalizando a prévia…"; };
            using var registration = token.Register(() => DispatcherQueue.TryEnqueue(() => _preview?.RequestClose()));
            try { await _preview.ShowPreviewAsync(); }
            finally { _preview = null; }
        });
    }

    private void Queue_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (_realized.Remove(args.ItemContainer, out var old)) _thumbnails.Cancel(old);
        if (args.InRecycleQueue || args.Item is not QueueItem item) return;
        _realized[args.ItemContainer] = item;
        if (CanInteract) _thumbnails.Request(item);
    }
    private void Queue_SelectionChanged(object sender, SelectionChangedEventArgs e)
    { if (_ready) RemoveSelectedButton.IsEnabled = CanInteract && QueueList.SelectedItems.Count > 0; }
    private bool MatchesFilter(QueueItem item) => (QueueFilter)Math.Max(0, FilterBox.SelectedIndex) switch
    {
        QueueFilter.Completed => item.State == JobState.Completed,
        QueueFilter.Failed => item.State == JobState.Failed,
        QueueFilter.Pending => item.State is not JobState.Completed and not JobState.Failed,
        _ => true
    };
    private void Filter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        _visible.Clear();
        _visibleIds.Clear();
        foreach (var item in _all) if (MatchesFilter(item)) { _visible.Add(item); _visibleIds.Add(item.Id); }
        UpdateQueueText();
    }
    private void UpdateFilteredItem(QueueItem item)
    {
        bool wanted = MatchesFilter(item);
        bool exists = _visibleIds.Contains(item.Id);
        if (wanted && !exists) { _visible.Add(item); _visibleIds.Add(item.Id); }
        else if (!wanted && exists) { _visible.Remove(item); _visibleIds.Remove(item.Id); }
    }
    private async void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (CanInteract && sender is FrameworkElement { Tag: QueueItem item })
            await InteractAsync(_ => RemoveItemsAsync(new[] { item }));
    }
    private async void RemoveSelected_Click(object sender, RoutedEventArgs e)
    {
        if (!CanInteract) return;
        var items = QueueList.SelectedItems.OfType<QueueItem>().ToArray();
        await InteractAsync(_ => RemoveItemsAsync(items));
    }
    private async void Clear_Click(object sender, RoutedEventArgs e)
    {
        if (!CanInteract) return;
        var items = _all.ToArray();
        await InteractAsync(_ => RemoveItemsAsync(items));
    }
    private async Task RemoveItemsAsync(QueueItem[] items)
    {
        StatusText.Text = "Atualizando a fila…";
        var readers = new List<Task>();
        int removed = 0;
        foreach (var item in items)
        {
            readers.Add(_thumbnails.Remove(item));
            _all.Remove(item); _visible.Remove(item); _visibleIds.Remove(item.Id); _byId.Remove(item.Id); _paths.Remove(item.Source);
            foreach (var key in _realized.Where(x => x.Value == item).Select(x => x.Key).ToArray()) _realized.Remove(key);
            if (++removed % 40 == 0) await Task.Yield();
        }
        // Aguarda quem estava lendo a miniatura antes de apagar uma imagem colada.
        await Task.WhenAll(readers);
        await Task.Run(() => { foreach (var item in items) if (item.IsTemporary) _cache?.Release(item.Source); });
        ResetBatchDisplay(); RefreshControls(); UpdateTotals();
    }

    private void Format_SelectionChanged(object sender, SelectionChangedEventArgs e)
    { if (_ready) { SettingsChanged(); RefreshControls(); } }
    private void Quality_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    { if (_ready) { SettingsChanged(); UpdateQuality(); } }
    private void Setting_Changed(object sender, RoutedEventArgs e) { if (_ready) SettingsChanged(); }
    private void Dimension_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    { if (_ready) SettingsChanged(); }
    private void ResizeMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || _applying) return;
        _applying = true;
        bool percent = ResizeModeBox.SelectedIndex == (int)ResizeMode.Percentage;
        ResizeValueBox.Maximum = percent ? 1000 : ResizeMath.MaxSide;
        ResizeValueBox.Value = percent ? 50 : 1920;
        _applying = false;
        UpdateResizeControls(); SettingsChanged();
    }
    private void Background_ColorChanged(ColorPicker sender, ColorChangedEventArgs args)
    {
        if (!_ready) return;
        BackgroundSwatch.Color = args.NewColor;
        SettingsChanged();
    }
    private void UpdateFolder()
    {
        FolderText.Text = string.IsNullOrWhiteSpace(_folder) ? "Escolha uma pasta de destino" : _folder;
        ToolTipService.SetToolTip(FolderText, _folder);
    }
    private UserOptions CaptureSettings()
    {
        var mode = (ResizeMode)Math.Max(0, ResizeModeBox.SelectedIndex);
        uint value = 1920, height = 1080;
        if (mode != ResizeMode.Original)
        {
            if (!double.IsFinite(ResizeValueBox.Value) || ResizeValueBox.Value < 1)
                throw new ArgumentException("Informe um tamanho maior que zero.");
            value = checked((uint)Math.Round(ResizeValueBox.Value));
            if (mode == ResizeMode.FitBox)
            {
                if (!double.IsFinite(ResizeHeightBox.Value) || ResizeHeightBox.Value < 1)
                    throw new ArgumentException("Informe uma altura maior que zero.");
                height = checked((uint)Math.Round(ResizeHeightBox.Value));
            }
        }
        var color = BackgroundPicker.Color;
        return new UserOptions
        {
            Format = (FormatBox.SelectedItem as ExportFormat)?.Extension ?? "webp",
            Quality = (uint)Math.Round(QualitySlider.Value), Folder = _folder,
            Rename = RenameCheck.IsChecked == true, OpenFolder = OpenFolderCheck.IsChecked == true,
            Notify = NotifyCheck.IsChecked == true, IncludeSubfolders = SubfoldersCheck.IsChecked == true,
            Resize = new(mode, value, height, EnlargeCheck.IsChecked == true),
            Background = $"#{color.R:X2}{color.G:X2}{color.B:X2}"
        };
    }
    private ConversionOptions ToConversionOptions(UserOptions settings)
    {
        var format = _formats.FirstOrDefault(f => f.Extension == settings.Format)
            ?? throw new ArgumentException("Escolha um formato disponível.");
        return new(settings.Folder, format, settings.Quality, settings.Rename, settings.Resize, settings.Background);
    }
    private void ApplySettings(UserOptions settings)
    {
        bool previous = _applying; _applying = true;
        try
        {
            FormatBox.SelectedItem = _formats.FirstOrDefault(f => string.Equals(f.Extension, settings.Format, StringComparison.OrdinalIgnoreCase))
                ?? _formats.FirstOrDefault();
            QualitySlider.Value = Math.Clamp(settings.Quality, 1u, 100u);
            _folder = settings.Folder ?? "";
            RenameCheck.IsChecked = settings.Rename; OpenFolderCheck.IsChecked = settings.OpenFolder;
            NotifyCheck.IsChecked = settings.Notify; SubfoldersCheck.IsChecked = settings.IncludeSubfolders;
            var resize = settings.Resize ?? new();
            var mode = Enum.IsDefined(resize.Mode) ? resize.Mode : ResizeMode.Original;
            ResizeModeBox.SelectedIndex = (int)mode;
            ResizeValueBox.Maximum = mode == ResizeMode.Percentage ? 1000 : ResizeMath.MaxSide;
            ResizeValueBox.Value = Math.Clamp(resize.Value, 1u, (uint)ResizeValueBox.Maximum);
            ResizeHeightBox.Value = Math.Clamp(resize.Height, 1u, ResizeMath.MaxSide);
            EnlargeCheck.IsChecked = resize.AllowEnlarge;
            string hex = ImagePipeline.IsColor(settings.Background) ? settings.Background : "#FFFFFF";
            BackgroundPicker.Color = Windows.UI.Color.FromArgb(255, Convert.ToByte(hex.Substring(1, 2), 16),
                Convert.ToByte(hex.Substring(3, 2), 16), Convert.ToByte(hex.Substring(5, 2), 16));
            BackgroundSwatch.Color = BackgroundPicker.Color;
            UpdateFolder(); UpdateResizeControls(); UpdateQuality();
            _lastGood = CaptureSettings();
        }
        finally { _applying = previous; }
    }
    private void SettingsChanged()
    {
        if (!_ready || _applying || !_preferencesLoaded) return;
        try { _lastGood = CaptureSettings(); } catch (ArgumentException) { return; }
        _applying = true;
        ProfilesBox.SelectedItem = null;
        _applying = false;
        DeleteProfileButton.IsEnabled = false;
        _saveTimer.Stop(); _saveTimer.Start();
    }
    private async Task SavePreferencesAsync()
    {
        if (!_preferencesLoaded) return;
        try
        {
            try { _lastGood = CaptureSettings(); } catch (ArgumentException) { }
            await _preferences.SaveAsync(new AppPreferences { Last = _lastGood, Profiles = _profiles.ToList() });
        }
        catch (Exception ex)
        {
            Trace.WriteLine(ex);
            if (!_closeRequested) ShowNotice("Preferências não salvas", ex.Message, InfoBarSeverity.Warning);
        }
    }
    private void Profile_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || _applying) return;
        if (ProfilesBox.SelectedItem is NamedProfile profile)
        {
            ApplySettings(profile.Options);
            _saveTimer.Stop(); _saveTimer.Start();
        }
        RefreshControls();
    }
    private async void SaveProfile_Click(object sender, RoutedEventArgs e) => await InteractAsync(async token =>
    {
        var settings = CaptureSettings();
        var input = new TextBox { Header = "Nome do perfil", PlaceholderText = "Ex.: Fotos para compartilhar", MaxLength = 64 };
        var hint = new TextBlock { Text = "Inclui formato, qualidade, tamanho e pasta de destino. Um nome já existente atualiza o perfil.", TextWrapping = TextWrapping.Wrap };
        var panel = new StackPanel { Spacing = 12 }; panel.Children.Add(input); panel.Children.Add(hint);
        var dialog = new ContentDialog
        {
            Title = "Salvar perfil", Content = panel, PrimaryButtonText = "Salvar", CloseButtonText = "Cancelar",
            DefaultButton = ContentDialogButton.Primary, XamlRoot = Root.XamlRoot, RequestedTheme = ElementTheme.Dark,
            IsPrimaryButtonEnabled = false
        };
        input.TextChanged += (_, _) => dialog.IsPrimaryButtonEnabled = !string.IsNullOrWhiteSpace(input.Text);
        _profileDialog = dialog;
        ContentDialogResult result;
        try { result = await dialog.ShowAsync(); } finally { _profileDialog = null; }
        token.ThrowIfCancellationRequested();
        if (result != ContentDialogResult.Primary) return;
        string name = input.Text.Trim();
        var previous = _profiles.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        if (previous is null && _profiles.Count >= 64) throw new ArgumentException("O limite é de 64 perfis. Exclua um perfil para criar outro.");
        var profile = new NamedProfile(previous?.Id ?? Guid.NewGuid(), name, settings);
        _applying = true;
        if (previous is null) _profiles.Add(profile); else _profiles[_profiles.IndexOf(previous)] = profile;
        ProfilesBox.SelectedItem = profile;
        _applying = false;
        await SavePreferencesAsync();
    });
    private async void DeleteProfile_Click(object sender, RoutedEventArgs e)
    {
        if (!CanInteract || ProfilesBox.SelectedItem is not NamedProfile profile) return;
        _profiles.Remove(profile);
        await SavePreferencesAsync();
        RefreshControls();
    }

    private void UpdateQuality()
    {
        bool hasQuality = FormatBox.SelectedItem is ExportFormat { HasQuality: true };
        QualitySlider.IsEnabled = CanInteract && hasQuality;
        QualityText.Text = hasQuality ? $"{QualitySlider.Value:0}%" : "Sem perdas";
        QualityHint.Text = hasQuality ? "Mais qualidade preserva detalhes e pode gerar arquivos maiores."
            : "Este formato não usa o controle de qualidade.";
        bool opaque = FormatBox.SelectedItem is ExportFormat { SupportsAlpha: false };
        BackgroundButton.IsEnabled = CanInteract && opaque;
        BackgroundHint.Text = opaque ? "Substitui as áreas transparentes pela cor escolhida."
            : "Este formato mantém a transparência da imagem.";
    }
    private void UpdateResizeControls()
    {
        var mode = (ResizeMode)Math.Max(0, ResizeModeBox.SelectedIndex);
        ResizeValueBox.Visibility = mode == ResizeMode.Original ? Visibility.Collapsed : Visibility.Visible;
        ResizeHeightBox.Visibility = mode == ResizeMode.FitBox ? Visibility.Visible : Visibility.Collapsed;
        ResizeValueBox.Header = mode switch
        {
            ResizeMode.Width => "Largura (pixels)", ResizeMode.Height => "Altura (pixels)",
            ResizeMode.Percentage => "Porcentagem do tamanho original", ResizeMode.FitBox => "Largura máxima (pixels)",
            _ => "Maior lado (pixels)"
        };
        EnlargeCheck.IsEnabled = CanInteract && mode != ResizeMode.Original;
    }
    private void UpdateQueueText()
    {
        CountText.Text = _all.Count == 0 ? "Sua fila está vazia" : $"{_visible.Count} de {_all.Count} imagem(ns)";
        EmptyState.Visibility = _visible.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyTitle.Text = _all.Count == 0 ? "Tudo começa com uma imagem" : "Nenhuma imagem neste filtro";
        EmptyHint.Text = _all.Count == 0 ? "Adicione imagens, pastas ou use Ctrl+V." : "Escolha outro filtro para ver o restante da fila.";
    }
    private void RefreshControls()
    {
        if (!_ready || _closed) return;
        bool idle = CanInteract;
        foreach (Control control in new Control[] { AddButton, AddFolderButton, PasteButton, FolderButton,
            RenameCheck, OpenFolderCheck, NotifyCheck, SubfoldersCheck, ResizeModeBox, ResizeValueBox,
            ResizeHeightBox, ProfilesBox, SaveProfileButton }) control.IsEnabled = idle;
        FormatBox.IsEnabled = idle && _formats.Count > 0;
        ClearButton.IsEnabled = idle && _all.Count > 0;
        RemoveSelectedButton.IsEnabled = idle && QueueList.SelectedItems.Count > 0;
        DeleteProfileButton.IsEnabled = idle && ProfilesBox.SelectedItem is NamedProfile;
        FilterBox.IsEnabled = !_loading && !_closeRequested;
        int pending = _all.Count(i => i.State != JobState.Completed);
        StartButton.IsEnabled = idle && pending > 0 && FormatBox.SelectedItem is ExportFormat && !string.IsNullOrWhiteSpace(_folder);
        StartButton.Visibility = _batchRunning ? Visibility.Collapsed : Visibility.Visible;
        CancelButton.Visibility = _batchRunning ? Visibility.Visible : Visibility.Collapsed;
        CancelButton.IsEnabled = _batchRunning && _batchCancellation?.IsCancellationRequested == false;
        if (_rowInteraction != idle)
        {
            _rowInteraction = idle;
            foreach (var item in _all) item.SetInteraction(idle);
        }
        _thumbnails.SetEnabled(idle);
        UpdateQueueText(); UpdateQuality(); UpdateResizeControls();
        if (_closeRequested) StatusText.Text = "Encerrando… aguardando os arquivos temporários serem liberados.";
        else if (!_batchRunning && !_interacting)
            StatusText.Text = _lastBatchResult ?? (_loading ? "Preparando os formatos e suas preferências…"
                : _all.Count == 0 ? "Adicione imagens para começar."
                : string.IsNullOrWhiteSpace(_folder) ? "Escolha onde salvar suas imagens."
                : $"Pronto para converter {pending} imagem(ns).");
    }
    private void ResetBatchDisplay()
    {
        _batch = Array.Empty<QueueItem>(); _progress.Updates.Clear();
        _lastBatchResult = null;
        TotalProgress.Value = 0; TotalText.Text = "0%";
        SuccessIcon.Visibility = Visibility.Collapsed;
        _taskbar.Clear(); UpdateTotals();
    }
    private void UpdateTotals()
    {
        long before = 0, after = 0; int count = 0;
        foreach (var item in _all)
        {
            if (item.State != JobState.Completed || item.SourceBytes is null || item.OutputBytes is null) continue;
            before += item.SourceBytes.Value; after += item.OutputBytes.Value; count++;
        }
        TotalsText.Text = count == 0 ? "Clique na miniatura para comparar antes e depois."
            : $"{count} concluída(s): {FileSizes.Format(before)} → {FileSizes.Format(after)} · {FileSizes.Difference(before, after)}";
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (!CanInteract || string.IsNullOrWhiteSpace(_folder)) return;
        UserOptions settings; ConversionOptions options;
        try { settings = CaptureSettings(); options = ToConversionOptions(settings); }
        catch (Exception ex) { ShowNotice("Confira os ajustes", ex.Message, InfoBarSeverity.Warning); return; }
        _batch = _all.Where(i => i.State != JobState.Completed).ToArray();
        if (_batch.Length == 0) return;
        _batchRunning = true;
        _lastBatchResult = null;
        using var cancellation = new CancellationTokenSource();
        _batchCancellation = cancellation;
        _progress.Updates.Clear();
        foreach (var item in _batch)
        { item.Apply(new(item.Id, JobState.Queued, 0, "Na fila")); UpdateFilteredItem(item); }
        Notice.IsOpen = false; SuccessIcon.Visibility = Visibility.Collapsed;
        TotalProgress.Value = 0; TotalText.Text = "0%"; StatusText.Text = "Iniciando conversão…";
        _taskbar.Set(0); RefreshControls(); UpdateTotals();
        try
        {
            var jobs = _batch.Select(i => new WorkItem(i.Id, i.Source, i.Sequence, i.Name)).ToArray();
            var summary = await _engine.RunAsync(jobs, options, _progress, cancellation.Token);
            DrainProgress();
            string result = $"{summary.Completed} convertida(s), {summary.Failed} com erro, {summary.Cancelled} cancelada(s).";
            _lastBatchResult = result;
            bool success = summary.Completed == _batch.Length;
            StatusText.Text = result;
            if (!_closeRequested)
            {
                ShowNotice(success ? "Tudo pronto!" : "Lote finalizado", result, success ? InfoBarSeverity.Success : InfoBarSeverity.Warning);
                if (success) ShowSuccess();
                if (summary.Failed > 0) _taskbar.Set(100, TaskbarState.Error);
                else if (summary.Cancelled > 0) _taskbar.Set(TotalProgress.Value, TaskbarState.Paused);
                else _taskbar.Clear();
                if (settings.Notify)
                {
                    string? warning = CompletionNotification.Show("Carriola Images Converter", result);
                    if (warning is not null) Notice.Message += " " + warning;
                }
                if (settings.OpenFolder && summary.Completed > 0) await OpenOutputFolderAsync(settings.Folder);
            }
        }
        catch (Exception ex)
        {
            DrainProgress(); _taskbar.Set(TotalProgress.Value, TaskbarState.Error);
            if (!_closeRequested) ShowNotice("Não foi possível concluir o lote", ConversionEngine.DescribeError(ex), InfoBarSeverity.Error);
            _lastBatchResult = StatusText.Text = "A conversão foi interrompida.";
        }
        finally
        {
            _batchCancellation = null; _batchRunning = false;
            RefreshControls();
            await FinishCloseIfNeededAsync();
        }
    }
    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _batchCancellation?.Cancel();
        CancelButton.IsEnabled = false;
        _taskbar.Set(TotalProgress.Value, TaskbarState.Paused);
        StatusText.Text = "Cancelando… aguardando a operação atual terminar.";
    }
    private void DrainProgress()
    {
        bool changed = false;
        foreach (var id in _progress.Updates.Keys)
        {
            if (!_progress.Updates.TryRemove(id, out var update) || !_byId.TryGetValue(id, out var item)) continue;
            var previous = item.State;
            item.Apply(update);
            if (previous != item.State) UpdateFilteredItem(item);
            changed = true;
        }
        if (!changed || _batch.Length == 0) return;
        int finished = 0; double sum = 0;
        foreach (var item in _batch)
        {
            bool terminal = item.State is JobState.Completed or JobState.Failed or JobState.Cancelled;
            if (terminal) finished++;
            sum += item.State is JobState.Completed or JobState.Failed ? 100 : item.Percent;
        }
        double total = Math.Clamp(sum / _batch.Length, 0, 100);
        TotalProgress.Value = total; TotalText.Text = $"{total:0}%";
        _taskbar.Set(total, _batchCancellation?.IsCancellationRequested == true ? TaskbarState.Paused : TaskbarState.Normal);
        if (_batchRunning && !_closeRequested && _batchCancellation?.IsCancellationRequested != true)
            StatusText.Text = $"{finished} de {_batch.Length} imagem(ns) finalizada(s)";
        UpdateQueueText(); UpdateTotals();
    }
    private async Task OpenOutputFolderAsync(string path)
    {
        try
        {
            var folder = await StorageFolder.GetFolderFromPathAsync(path);
            if (_closeRequested) return;
            if (!await Launcher.LaunchFolderAsync(folder)) Notice.Message += $" Abra a pasta manualmente: {path}";
        }
        catch (Exception ex) { if (!_closeRequested) Notice.Message += " Imagens salvas; não foi possível abrir a pasta: " + ex.Message; }
    }
    private async void Window_Closing(AppWindow sender, AppWindowClosingEventArgs e)
    {
        if (_allowClose) return;
        e.Cancel = true;
        _closeRequested = true;
        CancelStartup();
        _batchCancellation?.Cancel(); _interactionCancellation?.Cancel();
        _preview?.RequestClose(); _profileDialog?.Hide();
        RefreshControls();
        await FinishCloseIfNeededAsync();
    }
    private async Task FinishCloseIfNeededAsync()
    {
        if (!_closeRequested || _loading || _batchRunning || _interacting || _finishingClose) return;
        _finishingClose = true;
        _timer.Stop(); _saveTimer.Stop();
        await _thumbnails.StopAsync();
        await SavePreferencesAsync();
        _dropAnimation?.Stop(); _successAnimation?.Stop();
        _taskbar.Dispose(); _cache?.Dispose();
        _allowClose = true;
        Close();
    }
    private void ShowNotice(string title, string message, InfoBarSeverity severity)
    {
        if (_closed) return;
        Notice.Title = title; Notice.Message = message; Notice.Severity = severity; Notice.IsOpen = true;
    }
    private void AnimateDrop(bool active)
    {
        if (!_ready || _closed || _dropActive == active) return;
        _dropActive = active;
        double target = active ? 1.015 : 1;
        DropTitle.Text = active ? "Solte para adicionar à fila" : "Arraste imagens ou pastas para cá";
        DropZone.BorderBrush = (Brush)Root.Resources[active ? "AccentBrush" : "OutlineBrush"];
        if (Math.Abs(DropScale.ScaleX - target) < 0.001) return;
        double current = DropScale.ScaleX;
        _dropAnimation?.Stop();
        if (!_uiSettings.AnimationsEnabled)
        {
            DropScale.ScaleX = target;
            DropScale.ScaleY = target;
            return;
        }
        _dropAnimation = new Storyboard();
        foreach (string property in new[] { "ScaleX", "ScaleY" })
        {
            var animation = new DoubleAnimation
            {
                From = current,
                To = target,
                Duration = new Duration(TimeSpan.FromMilliseconds(160)),
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut }
            };
            Storyboard.SetTarget(animation, DropScale);
            Storyboard.SetTargetProperty(animation, property);
            _dropAnimation.Children.Add(animation);
        }
        _dropAnimation.Begin();
    }

    private void ShowSuccess()
    {
        _successAnimation?.Stop();
        SuccessIcon.Visibility = Visibility.Visible;
        SuccessIcon.Opacity = 1;
        if (!_uiSettings.AnimationsEnabled) return;
        var animation = new DoubleAnimation
        {
            From = 0,
            To = 1,
            Duration = new Duration(TimeSpan.FromMilliseconds(260)),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(animation, SuccessIcon);
        Storyboard.SetTargetProperty(animation, "Opacity");
        _successAnimation = new Storyboard();
        _successAnimation.Children.Add(animation);
        _successAnimation.Begin();
    }

    // Uma atualização pendente por arquivo. Evita acumular milhares de callbacks na UI.
    private sealed class BufferedProgress : IProgress<ConversionUpdate>
    {
        public ConcurrentDictionary<Guid, ConversionUpdate> Updates { get; } = new();
        public void Report(ConversionUpdate value) => Updates[value.Id] = value;
    }
}
