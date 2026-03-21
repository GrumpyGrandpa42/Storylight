using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using StoryLight.App.Models;
using StoryLight.App.Services;
using StoryLight.App.Utils;

namespace StoryLight.App.ViewModels;

public sealed class MainWindowViewModel : ObservableObject, IDisposable
{
    private readonly IDocumentImporter _documentImporter;
    private readonly IReaderStateStore _stateStore;
    private readonly ITextToSpeechService _textToSpeechService;
    private readonly AppState _appState = new();
    private readonly List<ReaderPage> _pages = new();

    private NormalizedDocument? _currentDocument;
    private LibraryItem? _selectedLibraryItem;
    private string _documentTitle = "StoryLight";
    private string _documentSubtitle = "Import a book or document to begin reading.";
    private string _pageTitle = "No document open";
    private string _pageText = "Your reading page will appear here.";
    private string _status = "Ready.";
    private double _zoomLevel = 1.0;
    private int _currentPageIndex;
    private int _speechRate;
    private int? _activeSpeechPageIndex;
    private bool _isBusy;
    private bool _isLibraryCollapsed;
    private bool _isReaderOptionsOpen;
    private bool _isUpdatingSpeechSelection;
    private bool _continueToNextPage;
    private TtsVoiceInfo? _selectedTtsVoice;

    public MainWindowViewModel(IDocumentImporter documentImporter, IReaderStateStore stateStore, ITextToSpeechService textToSpeechService)
    {
        _documentImporter = documentImporter;
        _stateStore = stateStore;
        _textToSpeechService = textToSpeechService;

        LibraryItems = new ObservableCollection<LibraryItem>();
        TtsVoices = new ObservableCollection<TtsVoiceInfo>();
        SpeechRates = new ReadOnlyCollection<int>(new[] { -3, -2, -1, 0, 1, 2, 3 });

        ImportDocumentCommand = new RelayCommand(async () => await ImportDocumentAsync(), () => !IsBusy);
        OpenSelectedCommand = new RelayCommand(async () => await OpenSelectedAsync(), () => !IsBusy && SelectedLibraryItem is not null);
        RemoveSelectedCommand = new RelayCommand(RemoveSelected, () => !IsBusy && SelectedLibraryItem is not null);
        DeleteSavedEpubCommand = new RelayCommand(DeleteSavedEpub, CanDeleteSelectedEpub);
        ToggleLibraryPaneCommand = new RelayCommand(() => IsLibraryCollapsed = !IsLibraryCollapsed);
        ToggleReaderOptionsCommand = new RelayCommand(() => IsReaderOptionsOpen = !IsReaderOptionsOpen);
        OpenVoicesFolderCommand = new RelayCommand(OpenVoicesFolder, () => !IsBusy && OperatingSystem.IsWindows());
        PreviousPageCommand = new RelayCommand(() => ChangePage(-1), () => CurrentPageIndex > 0);
        NextPageCommand = new RelayCommand(() => ChangePage(1), () => CurrentPageIndex < PageCount - 1);
        ZoomInCommand = new RelayCommand(() => ChangeZoom(0.1), () => ZoomLevel < 5.0);
        ZoomOutCommand = new RelayCommand(() => ChangeZoom(-0.1), () => ZoomLevel > 0.25);
        ReadAloudCommand = new RelayCommand(async () => await ReadCurrentPageAsync(), () => !IsBusy && _pages.Count > 0 && _textToSpeechService.IsAvailable);
        PauseSpeechCommand = new RelayCommand(async () => await PauseSpeechAsync(), () => _textToSpeechService.IsAvailable && _textToSpeechService.IsSpeaking && !_textToSpeechService.IsPaused);
        ResumeSpeechCommand = new RelayCommand(async () => await ResumeSpeechAsync(), () => _textToSpeechService.IsAvailable && _textToSpeechService.IsPaused);
        StopSpeechCommand = new RelayCommand(async () => await StopSpeechAsync(), () => _textToSpeechService.IsAvailable && (_textToSpeechService.IsSpeaking || _textToSpeechService.IsPaused));
        _textToSpeechService.PlaybackCompleted += OnPlaybackCompleted;

        _ = LoadStateAsync();
    }

    public ObservableCollection<LibraryItem> LibraryItems { get; }
    public ObservableCollection<TtsVoiceInfo> TtsVoices { get; }

    public IReadOnlyList<int> SpeechRates { get; }

    public string DocumentTitle
    {
        get => _documentTitle;
        private set => SetProperty(ref _documentTitle, value);
    }

    public string DocumentSubtitle
    {
        get => _documentSubtitle;
        private set => SetProperty(ref _documentSubtitle, value);
    }

    public string PageTitle
    {
        get => _pageTitle;
        private set
        {
            if (SetProperty(ref _pageTitle, value))
            {
                RaisePropertyChanged(nameof(HasPageTitle));
            }
        }
    }

    public string PageText
    {
        get => _pageText;
        private set => SetProperty(ref _pageText, value);
    }

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public LibraryItem? SelectedLibraryItem
    {
        get => _selectedLibraryItem;
        set
        {
            if (SetProperty(ref _selectedLibraryItem, value))
            {
                RefreshCommandStates();
            }
        }
    }

    public double ZoomLevel
    {
        get => _zoomLevel;
        set
        {
            var clamped = ClampZoom(value);
            if (SetProperty(ref _zoomLevel, clamped))
            {
                RaisePropertyChanged(nameof(ReaderFontSize));
                RaisePropertyChanged(nameof(ZoomDisplay));
                RaisePropertyChanged(nameof(ZoomPercent));
                RepaginateCurrentDocument();
                RefreshCommandStates();
            }
        }
    }

    public double ReaderFontSize => 18 * ZoomLevel;

    public string ZoomDisplay => $"{ZoomLevel:P0}";

    public double ZoomPercent
    {
        get => Math.Round(ZoomLevel * 100);
        set => ZoomLevel = value / 100d;
    }

    public int CurrentPageIndex
    {
        get => _currentPageIndex;
        private set
        {
            if (SetProperty(ref _currentPageIndex, value))
            {
                RaisePropertyChanged(nameof(CurrentPageNumber));
                RaisePropertyChanged(nameof(PageCount));
                RaisePropertyChanged(nameof(PageStatus));
                RefreshCommandStates();
            }
        }
    }

    public int CurrentPageNumber => _pages.Count == 0 ? 0 : CurrentPageIndex + 1;

    public int PageCount => _pages.Count;

    public bool HasPageTitle => !string.IsNullOrWhiteSpace(PageTitle);

    public string PageStatus => _pages.Count == 0
        ? "No pages"
        : $"Page {CurrentPageNumber} of {PageCount}";

    public int SpeechRate
    {
        get => _speechRate;
        set
        {
            if (SetProperty(ref _speechRate, value))
            {
                _appState.Speech.Rate = value;
                _textToSpeechService.Rate = value;
                _ = PersistStateAsync();
            }
        }
    }

    public bool IsTextToSpeechAvailable => _textToSpeechService.IsAvailable;

    public bool ContinueToNextPage
    {
        get => _continueToNextPage;
        set
        {
            if (SetProperty(ref _continueToNextPage, value))
            {
                _appState.Speech.ContinueToNextPage = value;
                _ = PersistStateAsync();
            }
        }
    }

    public bool IsLibraryCollapsed
    {
        get => _isLibraryCollapsed;
        set
        {
            if (SetProperty(ref _isLibraryCollapsed, value))
            {
                RaisePropertyChanged(nameof(IsLibraryVisible));
                _appState.IsLibraryCollapsed = value;
                _ = PersistStateAsync();
            }
        }
    }

    public bool IsLibraryVisible => !IsLibraryCollapsed;

    public bool IsReaderOptionsOpen
    {
        get => _isReaderOptionsOpen;
        set => SetProperty(ref _isReaderOptionsOpen, value);
    }

    public TtsVoiceInfo? SelectedTtsVoice
    {
        get => _selectedTtsVoice;
        set
        {
            if (SetProperty(ref _selectedTtsVoice, value) && !_isUpdatingSpeechSelection)
            {
                _ = ApplySelectedVoiceAsync();
            }
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                RefreshCommandStates();
            }
        }
    }

    public RelayCommand ImportDocumentCommand { get; }
    public RelayCommand OpenSelectedCommand { get; }
    public RelayCommand RemoveSelectedCommand { get; }
    public RelayCommand DeleteSavedEpubCommand { get; }
    public RelayCommand ToggleLibraryPaneCommand { get; }
    public RelayCommand ToggleReaderOptionsCommand { get; }
    public RelayCommand OpenVoicesFolderCommand { get; }
    public RelayCommand PreviousPageCommand { get; }
    public RelayCommand NextPageCommand { get; }
    public RelayCommand ZoomInCommand { get; }
    public RelayCommand ZoomOutCommand { get; }
    public RelayCommand ReadAloudCommand { get; }
    public RelayCommand PauseSpeechCommand { get; }
    public RelayCommand ResumeSpeechCommand { get; }
    public RelayCommand StopSpeechCommand { get; }

    public void HandlePreviousPageShortcut() => ChangePage(-1);

    public void HandleNextPageShortcut() => ChangePage(1);

    public void HandleZoomInShortcut() => ChangeZoom(0.1);

    public void HandleZoomOutShortcut() => ChangeZoom(-0.1);

    public void SelectLibraryItem(LibraryItem? item)
    {
        SelectedLibraryItem = item;
    }

    public void HandleWindowActivated()
    {
        _ = RefreshTtsOptionsAsync(updateStatus: false, persistState: false);
    }

    public void Dispose()
    {
        _textToSpeechService.PlaybackCompleted -= OnPlaybackCompleted;
        _textToSpeechService.Dispose();
    }

    private async Task LoadStateAsync()
    {
        try
        {
            var state = await _stateStore.LoadAsync();
            _appState.DefaultZoomLevel = state.DefaultZoomLevel;
            _appState.IsLibraryCollapsed = state.IsLibraryCollapsed;
            _appState.Library = state.Library;
            _appState.ReadingPositions = state.ReadingPositions;
            _appState.Speech = state.Speech ?? new SpeechSettings();

            foreach (var item in _appState.Library.OrderByDescending(item => item.LastOpenedUtc))
            {
                item.RefreshDerivedProperties();
                LibraryItems.Add(item);
            }

            _isLibraryCollapsed = _appState.IsLibraryCollapsed;
            RaisePropertyChanged(nameof(IsLibraryCollapsed));
            RaisePropertyChanged(nameof(IsLibraryVisible));
            ZoomLevel = _appState.DefaultZoomLevel <= 0 ? 1.0 : _appState.DefaultZoomLevel;
            _speechRate = _appState.Speech.Rate;
            _continueToNextPage = _appState.Speech.ContinueToNextPage;
            RaisePropertyChanged(nameof(SpeechRate));
            RaisePropertyChanged(nameof(ContinueToNextPage));
            await _textToSpeechService.InitializeAsync(_appState.Speech);
            await ReloadTtsOptionsAsync();
            Status = _textToSpeechService.IsAvailable
                ? LibraryItems.Count == 0 ? "Ready. Import a document to begin." : "Library restored."
                : _textToSpeechService.StatusSummary;
        }
        catch (Exception ex)
        {
            Status = $"Unable to load library state: {ex.Message}";
        }
    }

    private async Task ImportDocumentAsync()
    {
        var path = await PickFileAsync();
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        await OpenDocumentAsync(path);
    }

    private async Task OpenSelectedAsync()
    {
        if (SelectedLibraryItem is null)
        {
            return;
        }

        await OpenDocumentAsync(SelectedLibraryItem.SourcePath);
    }

    private async Task OpenDocumentAsync(string path)
    {
        IsBusy = true;
        Status = "Importing document...";

        try
        {
            if (!File.Exists(path))
            {
                Status = "The selected file no longer exists.";
                return;
            }

            var document = await _documentImporter.ImportAsync(path);
            _currentDocument = document;

            var item = UpsertLibraryItem(document);
            SelectedLibraryItem = item;

            DocumentTitle = document.Metadata.Title;
            DocumentSubtitle = string.IsNullOrWhiteSpace(document.Metadata.Author)
                ? $"{document.Metadata.Format} document"
                : $"{document.Metadata.Author} · {document.Metadata.Format}";

            var savedPosition = _appState.ReadingPositions.FirstOrDefault(position => position.DocumentId == item.Id);
            if (savedPosition is not null && savedPosition.ZoomLevel > 0)
            {
                _zoomLevel = ClampZoom(savedPosition.ZoomLevel);
                RaisePropertyChanged(nameof(ZoomLevel));
                RaisePropertyChanged(nameof(ReaderFontSize));
                RaisePropertyChanged(nameof(ZoomDisplay));
                RaisePropertyChanged(nameof(ZoomPercent));
            }

            BuildPages(document, savedPosition?.PageIndex ?? 0);

            item.LastOpenedUtc = DateTimeOffset.UtcNow;
            item.ProgressPercent = CalculateProgressPercent();
            item.RefreshDerivedProperties();

            await PersistStateAsync();
            Status = $"Opened {Path.GetFileName(path)}.";
        }
        catch (Exception ex)
        {
            Status = $"Unable to open document: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private LibraryItem UpsertLibraryItem(NormalizedDocument document)
    {
        var existing = _appState.Library.FirstOrDefault(item =>
            item.SourcePath.Equals(document.SourcePath, StringComparison.OrdinalIgnoreCase));

        if (existing is null)
        {
            existing = new LibraryItem
            {
                Id = Guid.NewGuid().ToString("N"),
                SourcePath = document.SourcePath,
                Format = document.Metadata.Format,
                Title = document.Metadata.Title,
                Subtitle = document.Metadata.Author ?? Path.GetFileName(document.SourcePath),
                CoverImageData = document.Metadata.CoverImageData,
                LastOpenedUtc = DateTimeOffset.UtcNow
            };

            _appState.Library.Add(existing);
            LibraryItems.Insert(0, existing);
        }
        else
        {
            existing.Title = document.Metadata.Title;
            existing.Subtitle = document.Metadata.Author ?? Path.GetFileName(document.SourcePath);
            existing.CoverImageData = document.Metadata.CoverImageData;
            existing.LastOpenedUtc = DateTimeOffset.UtcNow;
            existing.RefreshDerivedProperties();

            var existingIndex = LibraryItems.IndexOf(existing);
            if (existingIndex > 0)
            {
                LibraryItems.Move(existingIndex, 0);
            }
        }

        return existing;
    }

    private void RemoveSelected()
    {
        if (SelectedLibraryItem is null)
        {
            return;
        }

        var item = SelectedLibraryItem;
        RemoveItemFromLibrary(item);
        Status = $"Removed {item.Title} from the library.";
    }

    private void DeleteSavedEpub()
    {
        if (SelectedLibraryItem is null || !CanDeleteSelectedEpub())
        {
            return;
        }

        var item = SelectedLibraryItem;

        try
        {
            if (File.Exists(item.SourcePath))
            {
                File.Delete(item.SourcePath);
            }

            RemoveItemFromLibrary(item);
            Status = $"Deleted saved EPUB: {item.Title}.";
        }
        catch (Exception ex)
        {
            Status = $"Unable to delete EPUB: {ex.Message}";
        }
    }

    private void OpenVoicesFolder()
    {
        try
        {
            var folderPath = _textToSpeechService.VoicesFolderPath;
            if (string.IsNullOrWhiteSpace(folderPath))
            {
                Status = "Voice folders are not available on this platform.";
                return;
            }

            Directory.CreateDirectory(folderPath);
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{folderPath}\"",
                UseShellExecute = true
            });

            Status = $"Add a sherpa voice folder under {folderPath}, then return to StoryLight to refresh voices.";
        }
        catch (Exception ex)
        {
            Status = $"Unable to open voices folder: {ex.Message}";
        }
    }

    private void ChangePage(int delta)
    {
        if (_pages.Count == 0)
        {
            return;
        }

        var nextIndex = Math.Clamp(CurrentPageIndex + delta, 0, _pages.Count - 1);
        if (nextIndex == CurrentPageIndex)
        {
            return;
        }

        CurrentPageIndex = nextIndex;
        ShowCurrentPage();
        _ = PersistStateAsync();
    }

    private void ChangeZoom(double delta)
    {
        ZoomLevel = ClampZoom(ZoomLevel + delta);
    }

    private void RepaginateCurrentDocument()
    {
        if (_currentDocument is null)
        {
            return;
        }

        var currentSection = _pages.Count == 0 ? 0 : _pages[CurrentPageIndex].SectionIndex;
        BuildPages(_currentDocument, FindPageIndexForSection(currentSection));
        _appState.DefaultZoomLevel = ZoomLevel;
        _ = PersistStateAsync();
    }

    private void BuildPages(NormalizedDocument document, int initialPageIndex)
    {
        _pages.Clear();
        var charsPerPage = GetCharactersPerPage(document.Metadata.Format);

        for (var sectionIndex = 0; sectionIndex < document.Sections.Count; sectionIndex++)
        {
            var section = document.Sections[sectionIndex];

            if (section.PreserveAsPage || document.Metadata.Format == DocumentFormat.Pdf)
            {
                _pages.Add(new ReaderPage(section.Title, section.Text, sectionIndex, section.Anchor));
                continue;
            }

            foreach (var pageText in SplitIntoPages(section.Text, charsPerPage))
            {
                _pages.Add(new ReaderPage(section.Title, pageText, sectionIndex, section.Anchor));
            }
        }

        if (_pages.Count == 0)
        {
            _pages.Add(new ReaderPage(document.Metadata.Title, "No readable content was found.", 0, "empty"));
        }

        CurrentPageIndex = Math.Clamp(initialPageIndex, 0, _pages.Count - 1);
        ShowCurrentPage();
    }

    private void ShowCurrentPage()
    {
        if (_pages.Count == 0)
        {
            PageTitle = "No document open";
            PageText = "Your reading page will appear here.";
            return;
        }

        var page = _pages[CurrentPageIndex];
        PageTitle = ShouldHidePageTitle(page) ? string.Empty : page.Title;
        PageText = page.Text;

        if (SelectedLibraryItem is not null)
        {
            SelectedLibraryItem.ProgressPercent = CalculateProgressPercent();
            SelectedLibraryItem.LastOpenedUtc = DateTimeOffset.UtcNow;
            SelectedLibraryItem.RefreshDerivedProperties();
        }
    }

    private async Task ApplySelectedVoiceAsync()
    {
        if (SelectedTtsVoice is null)
        {
            return;
        }

        await _textToSpeechService.SetVoiceAsync(SelectedTtsVoice.Id);
        _appState.Speech.VoiceId = SelectedTtsVoice.Id;
        await PersistStateAsync();
        Status = $"Voice selected: {SelectedTtsVoice.DisplayName}.";
    }

    private async Task RefreshTtsOptionsAsync(bool updateStatus, bool persistState)
    {
        await _textToSpeechService.RefreshAsync();
        await ReloadTtsOptionsAsync();

        if (SelectedTtsVoice is not null)
        {
            _appState.Speech.VoiceId = SelectedTtsVoice.Id;
        }
        else
        {
            _appState.Speech.VoiceId = null;
        }

        if (persistState)
        {
            await PersistStateAsync();
        }

        if (updateStatus || !_textToSpeechService.IsAvailable)
        {
            Status = _textToSpeechService.StatusSummary;
        }
    }

    private Task ReloadTtsOptionsAsync()
    {
        _isUpdatingSpeechSelection = true;
        try
        {
            TtsVoices.Clear();
            foreach (var voice in _textToSpeechService.Voices)
            {
                TtsVoices.Add(voice);
            }

            SelectedTtsVoice = TtsVoices.FirstOrDefault(voice => voice.Id == _textToSpeechService.SelectedVoiceId)
                ?? TtsVoices.FirstOrDefault();
            RaisePropertyChanged(nameof(IsTextToSpeechAvailable));
        }
        finally
        {
            _isUpdatingSpeechSelection = false;
            RefreshCommandStates();
        }

        return Task.CompletedTask;
    }

    private async Task ReadCurrentPageAsync()
    {
        if (_pages.Count == 0)
        {
            return;
        }

        if (!_textToSpeechService.IsAvailable)
        {
            Status = _textToSpeechService.StatusSummary;
            RefreshCommandStates();
            return;
        }

        try
        {
            await _textToSpeechService.SpeakAsync(_pages[CurrentPageIndex].Text);
            _activeSpeechPageIndex = CurrentPageIndex;
            Status = "Reading current page aloud.";
            _ = PrefetchNextPageAsync(CurrentPageIndex);
        }
        catch (Exception ex)
        {
            Status = $"Read-aloud failed: {ex.Message}";
        }

        RefreshCommandStates();
    }

    private async Task PrefetchNextPageAsync(int currentPageIndex)
    {
        if (!ContinueToNextPage || currentPageIndex >= _pages.Count - 1)
        {
            return;
        }

        try
        {
            await _textToSpeechService.PrefetchAsync(_pages[currentPageIndex + 1].Text);
        }
        catch
        {
        }
    }

    private async Task PauseSpeechAsync()
    {
        await _textToSpeechService.PauseAsync();
        Status = "Read-aloud paused.";
        RefreshCommandStates();
    }

    private async Task ResumeSpeechAsync()
    {
        await _textToSpeechService.ResumeAsync();
        Status = "Read-aloud resumed.";
        RefreshCommandStates();
    }

    private async Task StopSpeechAsync()
    {
        await _textToSpeechService.StopAsync();
        _activeSpeechPageIndex = null;
        Status = "Read-aloud stopped.";
        RefreshCommandStates();
    }

    private void OnPlaybackCompleted(object? sender, EventArgs e)
    {
        _ = Dispatcher.UIThread.InvokeAsync(async () =>
        {
            RefreshCommandStates();

            if (!ContinueToNextPage || _activeSpeechPageIndex is null)
            {
                _activeSpeechPageIndex = null;
                return;
            }

            if (_activeSpeechPageIndex.Value != CurrentPageIndex)
            {
                _activeSpeechPageIndex = null;
                return;
            }

            if (CurrentPageIndex >= PageCount - 1)
            {
                _activeSpeechPageIndex = null;
                Status = "Reached the end of the document.";
                return;
            }

            ChangePage(1);
            await ReadCurrentPageAsync();
        });
    }

    private async Task PersistStateAsync()
    {
        _appState.DefaultZoomLevel = ZoomLevel;
        _appState.IsLibraryCollapsed = IsLibraryCollapsed;

        if (SelectedLibraryItem is not null)
        {
            var position = _appState.ReadingPositions.FirstOrDefault(entry => entry.DocumentId == SelectedLibraryItem.Id);
            if (position is null)
            {
                position = new ReadingPosition
                {
                    DocumentId = SelectedLibraryItem.Id
                };
                _appState.ReadingPositions.Add(position);
            }

            position.PageIndex = CurrentPageIndex;
            position.SectionIndex = _pages.Count == 0 ? 0 : _pages[CurrentPageIndex].SectionIndex;
            position.ProgressPercent = CalculateProgressPercent();
            position.ZoomLevel = ZoomLevel;
        }

        await _stateStore.SaveAsync(_appState);
    }

    private async Task<string?> PickFileAsync()
    {
        var window = GetMainWindow();
        if (window?.StorageProvider is null)
        {
            Status = "Unable to open the file picker.";
            return null;
        }

        var files = await window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import book or document",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Supported formats")
                {
                    Patterns = new[]
                    {
                        "*.epub",
                        "*.txt",
                        "*.md",
                        "*.doc",
                        "*.docx",
                        "*.pdf"
                    }
                }
            }
        });

        return files.FirstOrDefault()?.Path.LocalPath;
    }

    private static Window? GetMainWindow()
    {
        return (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
    }

    private double CalculateProgressPercent()
    {
        if (_pages.Count == 0)
        {
            return 0;
        }

        return (CurrentPageIndex + 1d) / _pages.Count;
    }

    private static double ClampZoom(double value)
    {
        return Math.Clamp(Math.Round(value, 2), 0.25, 5.0);
    }

    private int GetCharactersPerPage(DocumentFormat format)
    {
        if (format == DocumentFormat.Pdf)
        {
            return int.MaxValue;
        }

        var baseCharacters = 2600d / ZoomLevel;
        return Math.Max(800, (int)Math.Round(baseCharacters));
    }

    private static IEnumerable<string> SplitIntoPages(string text, int charactersPerPage)
    {
        var paragraphs = text.Split(new[] { "\n\n" }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var buffer = new List<string>();
        var currentLength = 0;

        foreach (var paragraph in paragraphs)
        {
            if (currentLength > 0 && currentLength + paragraph.Length > charactersPerPage)
            {
                yield return string.Join(Environment.NewLine + Environment.NewLine, buffer);
                buffer.Clear();
                currentLength = 0;
            }

            if (paragraph.Length > charactersPerPage)
            {
                foreach (var chunk in ChunkLongParagraph(paragraph, charactersPerPage))
                {
                    if (buffer.Count > 0)
                    {
                        yield return string.Join(Environment.NewLine + Environment.NewLine, buffer);
                        buffer.Clear();
                    }

                    yield return chunk;
                }

                currentLength = 0;
                continue;
            }

            buffer.Add(paragraph);
            currentLength += paragraph.Length + 2;
        }

        if (buffer.Count > 0)
        {
            yield return string.Join(Environment.NewLine + Environment.NewLine, buffer);
        }
    }

    private static IEnumerable<string> ChunkLongParagraph(string paragraph, int chunkSize)
    {
        var start = 0;
        while (start < paragraph.Length)
        {
            var length = Math.Min(chunkSize, paragraph.Length - start);
            yield return paragraph.Substring(start, length).Trim();
            start += length;
        }
    }

    private int FindPageIndexForSection(int sectionIndex)
    {
        for (var index = 0; index < _pages.Count; index++)
        {
            if (_pages[index].SectionIndex == sectionIndex)
            {
                return index;
            }
        }

        return 0;
    }

    private static bool ShouldHidePageTitle(ReaderPage page)
    {
        if (string.IsNullOrWhiteSpace(page.Title) || string.IsNullOrWhiteSpace(page.Text))
        {
            return true;
        }

        var normalizedTitle = NormalizeComparisonText(page.Title);
        var normalizedText = NormalizeComparisonText(page.Text);
        return normalizedText.StartsWith(normalizedTitle, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeComparisonText(string text)
    {
        return string.Concat(text.Where(char.IsLetterOrDigit)).ToUpperInvariant();
    }

    private void RefreshCommandStates()
    {
        OpenSelectedCommand.RaiseCanExecuteChanged();
        RemoveSelectedCommand.RaiseCanExecuteChanged();
        DeleteSavedEpubCommand.RaiseCanExecuteChanged();
        ToggleLibraryPaneCommand.RaiseCanExecuteChanged();
        ToggleReaderOptionsCommand.RaiseCanExecuteChanged();
        OpenVoicesFolderCommand.RaiseCanExecuteChanged();
        PreviousPageCommand.RaiseCanExecuteChanged();
        NextPageCommand.RaiseCanExecuteChanged();
        ZoomInCommand.RaiseCanExecuteChanged();
        ZoomOutCommand.RaiseCanExecuteChanged();
        ReadAloudCommand.RaiseCanExecuteChanged();
        PauseSpeechCommand.RaiseCanExecuteChanged();
        ResumeSpeechCommand.RaiseCanExecuteChanged();
        StopSpeechCommand.RaiseCanExecuteChanged();
        ImportDocumentCommand.RaiseCanExecuteChanged();
    }

    private bool CanDeleteSelectedEpub()
    {
        return !IsBusy
            && SelectedLibraryItem is not null
            && SelectedLibraryItem.Format == DocumentFormat.Epub
            && SelectedLibraryItem.SourcePath.EndsWith(".epub", StringComparison.OrdinalIgnoreCase)
            && File.Exists(SelectedLibraryItem.SourcePath);
    }

    private void RemoveItemFromLibrary(LibraryItem item)
    {
        LibraryItems.Remove(item);
        _appState.Library.Remove(item);
        _appState.ReadingPositions.RemoveAll(position => position.DocumentId == item.Id);

        if (_currentDocument?.SourcePath.Equals(item.SourcePath, StringComparison.OrdinalIgnoreCase) == true)
        {
            _currentDocument = null;
            _pages.Clear();
            CurrentPageIndex = 0;
            PageTitle = "No document open";
            PageText = "Your reading page will appear here.";
            DocumentTitle = "StoryLight";
            DocumentSubtitle = "Import a book or document to begin reading.";
        }

        SelectedLibraryItem = LibraryItems.FirstOrDefault();
        _ = PersistStateAsync();
    }
}
