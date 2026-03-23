using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
using HarborSentinel.Viewer.Models;
using HarborSentinel.Viewer.Services;
using Microsoft.UI.Dispatching;
using Windows.Storage.Pickers;

namespace HarborSentinel.Viewer.Views
{
    public sealed partial class MainPage : Page
    {
        public ObservableCollection<LogRecordView> FilteredItems { get; } = new ObservableCollection<LogRecordView>();
        public ObservableCollection<FileSummaryItem> SourceFiles { get; } = new ObservableCollection<FileSummaryItem>();
        public ObservableCollection<IncidentSummaryItem> IncidentItems { get; } = new ObservableCollection<IncidentSummaryItem>();

        private readonly List<LogRecordView> _allItems = new List<LogRecordView>();
        private readonly LogFileReader _reader = new LogFileReader();
        private CancellationTokenSource? _loadCts;
        private readonly DispatcherQueueTimer _filterDebounceTimer;
        private DatePreset _datePreset = DatePreset.All;
        private int _contextRadius = 3;
        private IncidentSelectionState? _activeIncident;
        private readonly Dictionary<long, List<LogRecordView>> _messagesByChat = new Dictionary<long, List<LogRecordView>>();

        public MainPage()
        {
            InitializeComponent();
            FilesListView.ItemsSource = SourceFiles;
            RecordsListView.ItemsSource = FilteredItems;
            IncidentsListView.ItemsSource = IncidentItems;

            _filterDebounceTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
            _filterDebounceTimer.Interval = TimeSpan.FromMilliseconds(120);
            _filterDebounceTimer.Tick += (_, _) =>
            {
                _filterDebounceTimer.Stop();
                ApplyFilters();
            };

            ChatFilterComboBox.Items.Add("Все чаты");
            ChatFilterComboBox.SelectedIndex = 0;

            LabelFilterComboBox.Items.Add("Все метки");
            LabelFilterComboBox.SelectedIndex = 0;

            ContextRadiusTextBox.Text = _contextRadius.ToString();
            ShowDetails(null);
            SetStatus("Выберите файлы логов для загрузки.");
            UpdateSummary();
        }

        private async void OpenFilesClick(object sender, RoutedEventArgs e)
        {
            var picker = new FileOpenPicker();
            picker.FileTypeFilter.Add(".jsonl");
            picker.FileTypeFilter.Add(".gz");
            picker.FileTypeFilter.Add(".json");
            picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;

            InitializePicker(picker);

            var files = await picker.PickMultipleFilesAsync();
            if (files == null || files.Count == 0)
            {
                return;
            }

            var paths = files.Select(file => file.Path).ToArray();
            await LoadAndMergeAsync(paths).ConfigureAwait(false);
        }

        private async void OpenFolderClick(object sender, RoutedEventArgs e)
        {
            var picker = new FolderPicker();
            picker.FileTypeFilter.Add("*");
            picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;

            InitializePicker(picker);

            var folder = await picker.PickSingleFolderAsync();
            if (folder == null)
            {
                return;
            }

            string[] files;
            try
            {
                files = Directory
                    .EnumerateFiles(folder.Path, "*.*", SearchOption.AllDirectories)
                    .Where(path =>
                        path.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase) ||
                        path.EndsWith(".jsonl.gz", StringComparison.OrdinalIgnoreCase) ||
                        path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                    .ToArray();
            }
            catch (Exception ex)
            {
                SetStatus($"Ошибка чтения папки: {ex.Message}");
                return;
            }

            if (files.Length == 0)
            {
                SetStatus("В выбранной папке нет подходящих файлов.");
                return;
            }

            await LoadAndMergeAsync(files).ConfigureAwait(false);
        }

        private void ClearClick(object sender, RoutedEventArgs e)
        {
            _loadCts?.Cancel();
            _allItems.Clear();
            SourceFiles.Clear();
            FilteredItems.Clear();
            IncidentItems.Clear();
            _activeIncident = null;
            _messagesByChat.Clear();

            ChatFilterComboBox.Items.Clear();
            ChatFilterComboBox.Items.Add("Все чаты");
            ChatFilterComboBox.SelectedIndex = 0;

            LabelFilterComboBox.Items.Clear();
            LabelFilterComboBox.Items.Add("Все метки");
            LabelFilterComboBox.SelectedIndex = 0;

            SearchTextBox.Text = string.Empty;
            UserIdTextBox.Text = string.Empty;
            FlaggedOnlySwitch.IsOn = false;
            _datePreset = DatePreset.All;
            ContextRadiusTextBox.Text = _contextRadius.ToString();

            RecordsListView.SelectedItem = null;
            ShowDetails(null);
            SetStatus("Очищено. Готово к новой загрузке.");
            UpdateSummary();
        }

        private async void ExportJsonlClick(object sender, RoutedEventArgs e)
        {
            if (FilteredItems.Count == 0)
            {
                SetStatus("Нет данных для экспорта.");
                return;
            }

            var picker = new FileSavePicker
            {
                SuggestedFileName = $"viewer-export-{DateTime.Now:yyyyMMdd-HHmmss}.jsonl"
            };
            picker.FileTypeChoices.Add("JSON Lines", new List<string> { ".jsonl" });
            InitializePicker(picker);

            var file = await picker.PickSaveFileAsync();
            if (file == null)
            {
                return;
            }

            var lines = new StringBuilder();
            foreach (var item in FilteredItems)
            {
                if (!string.IsNullOrWhiteSpace(item.RawJson))
                {
                    lines.AppendLine(item.RawJson);
                    continue;
                }

                var fallback = JsonSerializer.Serialize(new
                {
                    timestamp = item.Timestamp?.UtcDateTime.ToString("O"),
                    chatId = item.ChatId,
                    chatTitle = item.ChatTitle,
                    userId = item.UserId,
                    username = item.Username,
                    messageId = item.MessageId,
                    text = item.Text,
                    flagged = item.Flagged,
                    label = item.Label,
                    confidence = item.Confidence,
                    reason = item.Reason,
                    sourceFile = item.SourceFile
                });
                lines.AppendLine(fallback);
            }

            await File.WriteAllTextAsync(file.Path, lines.ToString(), Encoding.UTF8);
            SetStatus($"Экспортировано JSONL: {FilteredItems.Count} записей.");
        }

        private async void ExportCsvClick(object sender, RoutedEventArgs e)
        {
            if (FilteredItems.Count == 0)
            {
                SetStatus("Нет данных для экспорта.");
                return;
            }

            var picker = new FileSavePicker
            {
                SuggestedFileName = $"viewer-export-{DateTime.Now:yyyyMMdd-HHmmss}.csv"
            };
            picker.FileTypeChoices.Add("CSV", new List<string> { ".csv" });
            InitializePicker(picker);

            var file = await picker.PickSaveFileAsync();
            if (file == null)
            {
                return;
            }

            var csv = new StringBuilder();
            csv.AppendLine("timestamp_utc,chat_id,chat_title,user_id,username,message_id,label,confidence,flagged,text,reason,source_file");
            foreach (var item in FilteredItems)
            {
                csv
                    .Append(EscapeCsv(item.Timestamp?.UtcDateTime.ToString("O"))).Append(',')
                    .Append(EscapeCsv(item.ChatId?.ToString())).Append(',')
                    .Append(EscapeCsv(item.ChatTitle)).Append(',')
                    .Append(EscapeCsv(item.UserId?.ToString())).Append(',')
                    .Append(EscapeCsv(item.Username)).Append(',')
                    .Append(EscapeCsv(item.MessageId?.ToString())).Append(',')
                    .Append(EscapeCsv(item.DisplayLabel)).Append(',')
                    .Append(EscapeCsv(item.Confidence?.ToString("0.####"))).Append(',')
                    .Append(EscapeCsv(item.Flagged ? "1" : "0")).Append(',')
                    .Append(EscapeCsv(item.Text)).Append(',')
                    .Append(EscapeCsv(item.Reason)).Append(',')
                    .Append(EscapeCsv(item.SourceFile))
                    .AppendLine();
            }

            await File.WriteAllTextAsync(file.Path, csv.ToString(), Encoding.UTF8);
            SetStatus($"Экспортировано CSV: {FilteredItems.Count} записей.");
        }

        private void PresetAllClick(object sender, RoutedEventArgs e)
        {
            _datePreset = DatePreset.All;
            FlaggedOnlySwitch.IsOn = false;
            ScheduleFilterApply();
        }

        private void PresetFlaggedClick(object sender, RoutedEventArgs e)
        {
            _datePreset = DatePreset.All;
            FlaggedOnlySwitch.IsOn = true;
            ScheduleFilterApply();
        }

        private void PresetTodayClick(object sender, RoutedEventArgs e)
        {
            _datePreset = DatePreset.Today;
            ScheduleFilterApply();
        }

        private void PresetLastHourClick(object sender, RoutedEventArgs e)
        {
            _datePreset = DatePreset.LastHour;
            ScheduleFilterApply();
        }

        private async Task LoadAndMergeAsync(IEnumerable<string> filePaths)
        {
            _loadCts?.Cancel();
            _loadCts?.Dispose();
            _loadCts = new CancellationTokenSource();
            var ct = _loadCts.Token;

            var paths = filePaths
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(Path.GetFullPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (paths.Length == 0)
            {
                return;
            }

            try
            {
                SetStatus($"Загрузка {paths.Length} файл(ов)...");
                var loadResult = await _reader.LoadFilesAsync(paths, ct).ConfigureAwait(false);

                await DispatcherQueue.EnqueueAsync(() =>
                {
                    var added = 0;
                    var bySource = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                    var existingSignatures = new HashSet<string>(
                        _allItems.Select(BuildSignature),
                        StringComparer.Ordinal);

                    foreach (var item in loadResult.Records)
                    {
                        var signature = BuildSignature(item);
                        if (!existingSignatures.Add(signature))
                        {
                            continue;
                        }

                        _allItems.Add(item);
                        added++;

                        if (!bySource.TryAdd(item.SourceFile, 1))
                        {
                            bySource[item.SourceFile]++;
                        }
                    }

                    foreach (var report in loadResult.Reports.OrderBy(item => item.FilePath, StringComparer.OrdinalIgnoreCase))
                    {
                        SourceFiles.Add(new FileSummaryItem
                        {
                            Name = Path.GetFileName(report.FilePath),
                            FullPath = report.FilePath,
                            AddedCount = bySource.TryGetValue(report.FilePath, out var addedBySource) ? addedBySource : 0,
                            ParsedCount = report.ParsedEntries,
                            SkippedCount = report.SkippedEntries,
                            ErrorCount = report.ErrorEntries,
                            ErrorMessage = report.ErrorMessage
                        });
                    }

                    RebuildIndexes();
                    RebuildFilterOptions();
                    ApplyFilters();
                    var parsedTotal = loadResult.Reports.Sum(item => item.ParsedEntries);
                    var skippedTotal = loadResult.Reports.Sum(item => item.SkippedEntries);
                    var errorTotal = loadResult.Reports.Sum(item => item.ErrorEntries);

                    SetStatus(
                        $"Загружено новых записей: {added}. " +
                        $"Всего в сессии: {_allItems.Count}. " +
                        $"Диагностика: parsed={parsedTotal}, skipped={skippedTotal}, errors={errorTotal}.");
                });
            }
            catch (OperationCanceledException)
            {
                await DispatcherQueue.EnqueueAsync(() => SetStatus("Загрузка отменена."));
            }
            catch (Exception ex)
            {
                await DispatcherQueue.EnqueueAsync(() => SetStatus($"Ошибка загрузки: {ex.Message}"));
            }
        }

        private void SearchTextChanged(object sender, TextChangedEventArgs e) => ScheduleFilterApply();
        private void UserIdTextChanged(object sender, TextChangedEventArgs e) => ScheduleFilterApply();
        private void ChatFilterSelectionChanged(object sender, SelectionChangedEventArgs e) => ScheduleFilterApply();
        private void LabelFilterSelectionChanged(object sender, SelectionChangedEventArgs e) => ScheduleFilterApply();
        private void FlaggedOnlyToggled(object sender, RoutedEventArgs e) => ScheduleFilterApply();

        private void ContextRadiusTextChanged(object sender, TextChangedEventArgs e)
        {
            var raw = ContextRadiusTextBox.Text?.Trim();
            if (!int.TryParse(raw, out var parsed))
            {
                return;
            }

            _contextRadius = Math.Clamp(parsed, 0, 20);
            if (_contextRadius != parsed)
            {
                ContextRadiusTextBox.Text = _contextRadius.ToString();
            }

            if (RecordsListView.SelectedItem is LogRecordView currentSelection)
            {
                ShowDetails(currentSelection);
            }
        }

        private void ScheduleFilterApply()
        {
            _filterDebounceTimer.Stop();
            _filterDebounceTimer.Start();
        }

        private void ApplyFilters()
        {
            var search = SearchTextBox.Text?.Trim() ?? string.Empty;
            var userFilter = UserIdTextBox.Text?.Trim();
            _ = long.TryParse(userFilter, out var userIdFilter);
            var useUserIdFilter = !string.IsNullOrWhiteSpace(userFilter) && long.TryParse(userFilter, out _);

            var selectedChat = ChatFilterComboBox.SelectedItem as ChatFilterItem;
            var selectedLabel = LabelFilterComboBox.SelectedItem as LabelFilterItem;

            var query = _allItems.AsEnumerable();

            if (FlaggedOnlySwitch.IsOn)
            {
                query = query.Where(item => item.Flagged);
            }

            if (selectedChat != null && selectedChat.ChatId.HasValue)
            {
                query = query.Where(item => item.ChatId == selectedChat.ChatId);
            }

            if (selectedLabel != null && !selectedLabel.IsAll)
            {
                query = query.Where(item =>
                    string.Equals(item.DisplayLabel, selectedLabel.Label, StringComparison.OrdinalIgnoreCase));
            }

            if (useUserIdFilter)
            {
                query = query.Where(item => item.UserId == userIdFilter);
            }

            if (_datePreset == DatePreset.Today)
            {
                var today = DateTime.Now.Date;
                query = query.Where(item =>
                    item.Timestamp.HasValue &&
                    item.Timestamp.Value.ToLocalTime().Date == today);
            }
            else if (_datePreset == DatePreset.LastHour)
            {
                var threshold = DateTimeOffset.Now.AddHours(-1);
                query = query.Where(item =>
                    item.Timestamp.HasValue &&
                    item.Timestamp.Value >= threshold);
            }

            if (!string.IsNullOrWhiteSpace(search))
            {
                query = query.Where(item =>
                    Contains(item.Text, search) ||
                    Contains(item.Reason, search) ||
                    Contains(item.DisplayChat, search) ||
                    Contains(item.DisplayUser, search) ||
                    Contains(item.DisplayLabel, search));
            }

            var filtered = query
                .OrderByDescending(item => item.Timestamp ?? DateTimeOffset.MinValue)
                .ToArray();

            var previouslySelected = RecordsListView.SelectedItem as LogRecordView;
            FilteredItems.Clear();
            foreach (var item in filtered)
            {
                FilteredItems.Add(item);
            }

            if (previouslySelected is not null && filtered.Contains(previouslySelected))
            {
                RecordsListView.SelectedItem = previouslySelected;
            }
            else if (filtered.Length > 0)
            {
                RecordsListView.SelectedItem = filtered[0];
            }
            else
            {
                RecordsListView.SelectedItem = null;
                ShowDetails(null);
            }

            RebuildIncidents(filtered);
            UpdateSummary();
        }

        private void RebuildFilterOptions()
        {
            var selectedChatId = (ChatFilterComboBox.SelectedItem as ChatFilterItem)?.ChatId;
            var selectedLabel = (LabelFilterComboBox.SelectedItem as LabelFilterItem)?.Label;

            var chats = _allItems
                .Where(item => item.ChatId.HasValue)
                .GroupBy(item => item.ChatId!.Value)
                .Select(group =>
                {
                    var latest = group.FirstOrDefault(item => !string.IsNullOrWhiteSpace(item.ChatTitle)) ?? group.First();
                    var title = latest.DisplayChat;
                    return new ChatFilterItem
                    {
                        ChatId = group.Key,
                        Name = $"{title} ({group.Key})"
                    };
                })
                .OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();

            ChatFilterComboBox.Items.Clear();
            ChatFilterComboBox.Items.Add(new ChatFilterItem { ChatId = null, Name = "Все чаты" });
            foreach (var chat in chats)
            {
                ChatFilterComboBox.Items.Add(chat);
            }

            var chatIndex = 0;
            for (var i = 0; i < ChatFilterComboBox.Items.Count; i++)
            {
                if (ChatFilterComboBox.Items[i] is ChatFilterItem item && item.ChatId == selectedChatId)
                {
                    chatIndex = i;
                    break;
                }
            }

            ChatFilterComboBox.SelectedIndex = chatIndex;

            var labels = _allItems
                .Select(item => item.DisplayLabel)
                .Where(label => !string.IsNullOrWhiteSpace(label))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(label => label, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            LabelFilterComboBox.Items.Clear();
            LabelFilterComboBox.Items.Add(new LabelFilterItem { Label = "Все метки", IsAll = true });
            foreach (var label in labels)
            {
                LabelFilterComboBox.Items.Add(new LabelFilterItem { Label = label, IsAll = false });
            }

            var labelIndex = 0;
            for (var i = 0; i < LabelFilterComboBox.Items.Count; i++)
            {
                if (LabelFilterComboBox.Items[i] is LabelFilterItem item &&
                    !item.IsAll &&
                    string.Equals(item.Label, selectedLabel, StringComparison.OrdinalIgnoreCase))
                {
                    labelIndex = i;
                    break;
                }
            }

            LabelFilterComboBox.SelectedIndex = labelIndex;
        }

        private void RebuildIncidents(IEnumerable<LogRecordView> scopedItems)
        {
            var previouslySelected = IncidentsListView.SelectedItem as IncidentSummaryItem;
            var preferredChatId = _activeIncident?.ChatId ?? previouslySelected?.ChatId;
            var preferredUserId = _activeIncident?.UserId ?? previouslySelected?.UserId;
            var incidentRows = scopedItems
                .Where(item => item.Flagged && item.UserId.HasValue && item.ChatId.HasValue)
                .GroupBy(item => new { ChatId = item.ChatId!.Value, UserId = item.UserId!.Value })
                .Select(group =>
                {
                    var latest = group
                        .OrderByDescending(item => item.Timestamp ?? DateTimeOffset.MinValue)
                        .First();
                    var labels = group
                        .Select(item => item.DisplayLabel)
                        .Where(label => !string.IsNullOrWhiteSpace(label))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Take(3)
                        .ToArray();
                    var maxConfidence = group
                        .Where(item => item.Confidence.HasValue)
                        .Select(item => item.Confidence!.Value)
                        .DefaultIfEmpty()
                        .Max();

                    return new IncidentSummaryItem
                    {
                        ChatId = group.Key.ChatId,
                        UserId = group.Key.UserId,
                        ChatDisplay = latest.DisplayChat,
                        UserDisplay = latest.DisplayUser,
                        LabelSummary = labels.Length == 0 ? latest.DisplayLabel : string.Join(", ", labels),
                        Count = group.Count(),
                        LastSeen = group.Max(item => item.Timestamp ?? DateTimeOffset.MinValue),
                        MaxConfidence = maxConfidence <= 0 ? null : maxConfidence
                    };
                })
                .OrderByDescending(item => item.LastSeen)
                .ThenBy(item => item.UserDisplay, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();

            IncidentItems.Clear();
            foreach (var incident in incidentRows)
            {
                IncidentItems.Add(incident);
            }

            if (incidentRows.Length == 0)
            {
                _activeIncident = null;
                IncidentsListView.SelectedItem = null;
                return;
            }

            IncidentSummaryItem? restored = null;
            if (preferredChatId.HasValue && preferredUserId.HasValue)
            {
                restored = incidentRows.FirstOrDefault(item =>
                    item.ChatId == preferredChatId &&
                    item.UserId == preferredUserId);
            }

            restored ??= incidentRows[0];
            var selectedIncident = IncidentItems.FirstOrDefault(item =>
                item.ChatId == restored.ChatId && item.UserId == restored.UserId);

            if (selectedIncident is null)
            {
                _activeIncident = null;
                IncidentsListView.SelectedItem = null;
                return;
            }

            if (!ReferenceEquals(IncidentsListView.SelectedItem, selectedIncident))
            {
                IncidentsListView.SelectedItem = selectedIncident;
            }

            _activeIncident = new IncidentSelectionState(selectedIncident.ChatId, selectedIncident.UserId);
        }

        private void IncidentsSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (IncidentsListView.SelectedItem is not IncidentSummaryItem incident)
            {
                return;
            }

            _activeIncident = new IncidentSelectionState(incident.ChatId, incident.UserId);
            SetStatus($"Инцидент выбран: user_id={incident.UserId}, chat_id={incident.ChatId}.");
        }

        private void SelectChatFilterItem(long chatId)
        {
            for (var i = 0; i < ChatFilterComboBox.Items.Count; i++)
            {
                if (ChatFilterComboBox.Items[i] is ChatFilterItem item && item.ChatId == chatId)
                {
                    ChatFilterComboBox.SelectedIndex = i;
                    return;
                }
            }
        }

        private void RebuildIndexes()
        {
            _messagesByChat.Clear();

            foreach (var row in _allItems)
            {
                if (row.ChatId.HasValue)
                {
                    if (!_messagesByChat.TryGetValue(row.ChatId.Value, out var chatList))
                    {
                        chatList = new List<LogRecordView>();
                        _messagesByChat[row.ChatId.Value] = chatList;
                    }

                    chatList.Add(row);
                }
            }

            foreach (var list in _messagesByChat.Values)
            {
                SortMessages(list);
            }
        }

        private static void SortMessages(List<LogRecordView> list)
        {
            list.Sort((left, right) =>
            {
                var leftTs = left.Timestamp ?? DateTimeOffset.MinValue;
                var rightTs = right.Timestamp ?? DateTimeOffset.MinValue;
                var compare = leftTs.CompareTo(rightTs);
                if (compare != 0)
                {
                    return compare;
                }

                compare = Nullable.Compare(left.MessageId, right.MessageId);
                if (compare != 0)
                {
                    return compare;
                }

                return string.Compare(left.SourceFile, right.SourceFile, StringComparison.OrdinalIgnoreCase);
            });
        }

        private void RecordsSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var selected = RecordsListView.SelectedItem as LogRecordView;
            ShowDetails(selected);
            if (selected?.ChatId.HasValue == true &&
                selected.UserId.HasValue &&
                IncidentItems.Any(item =>
                    item.ChatId == selected.ChatId.Value &&
                    item.UserId == selected.UserId.Value))
            {
                _activeIncident = new IncidentSelectionState(selected.ChatId.Value, selected.UserId.Value);
            }
        }

        private void ShowDetails(LogRecordView? item)
        {
            if (item == null)
            {
                DetailHeaderTextBlock.Text = "Выберите запись слева";
                DetailMetaTextBlock.Text = string.Empty;
                DetailAiTextBlock.Text = string.Empty;
                DetailTextBox.Text = string.Empty;
                RawJsonTextBox.Text = string.Empty;
                ContextTextBox.Text = string.Empty;
                DetailSourceFileTextBlock.Text = string.Empty;
                return;
            }

            DetailHeaderTextBlock.Text = $"{item.DisplayTime} • {item.DisplayHeader}";
            DetailMetaTextBlock.Text =
                $"Чат: {item.DisplayChatWithId}\n" +
                $"Пользователь: {item.DisplayUserWithId}\n" +
                $"Источник: {item.DisplaySourceInfo}";

            if (item.Flagged)
            {
                var reason = string.IsNullOrWhiteSpace(item.Reason)
                    ? "Причина не была возвращена моделью."
                    : item.Reason;

                DetailAiTextBlock.Text =
                    $"> ⚠ Вердикт ИИ: {item.DisplayLabel} ({item.DisplayConfidence})\n" +
                    $"> Причина: {reason}";
            }
            else
            {
                DetailAiTextBlock.Text = $"Вердикт ИИ: нарушений не выявлено ({item.DisplayConfidence})";
            }

            DetailTextBox.Text = string.IsNullOrWhiteSpace(item.Text) ? "(пусто)" : item.Text!;
            RawJsonTextBox.Text = PrettyJson(item.RawJson);
            ContextTextBox.Text = BuildContextText(item);
            DetailSourceFileTextBlock.Text = $"Источник: {item.SourceFile}";
        }

        private string BuildContextText(LogRecordView item)
        {
            if (!item.ChatId.HasValue)
            {
                return "(Контекст недоступен: отсутствует chat_id)";
            }

            if (!_messagesByChat.TryGetValue(item.ChatId.Value, out var chatMessages) || chatMessages.Count == 0)
            {
                return "(Контекст недоступен: нет сообщений по выбранному чату)";
            }

            var index = chatMessages.IndexOf(item);
            if (index < 0)
            {
                var signature = BuildSignature(item);
                index = chatMessages.FindIndex(row =>
                    string.Equals(BuildSignature(row), signature, StringComparison.Ordinal));
            }

            if (index < 0)
            {
                return "(Контекст недоступен: выбранная запись не найдена в общем наборе)";
            }

            var fromIndex = Math.Max(0, index - _contextRadius);
            var toIndex = Math.Min(chatMessages.Count - 1, index + _contextRadius);
            var contextBuilder = new StringBuilder();
            contextBuilder.AppendLine($"Контекст по чату {item.ChatId}: ±{_contextRadius} сообщений");
            contextBuilder.AppendLine();

            for (var pointer = fromIndex; pointer <= toIndex; pointer++)
            {
                var row = chatMessages[pointer];
                var marker = pointer == index ? ">>" : "  ";
                var text = string.IsNullOrWhiteSpace(row.Text) ? "—" : row.Text!.ReplaceLineEndings(" ");
                contextBuilder.AppendLine($"{marker} [{row.DisplayTime}] {row.DisplayUser}: {text}");
            }

            return contextBuilder.ToString().TrimEnd();
        }

        private void FocusIncidentFilterClick(object sender, RoutedEventArgs e)
        {
            if (!_activeIncident.HasValue)
            {
                SetStatus("Сначала выберите инцидент.");
                return;
            }

            var incident = _activeIncident.Value;
            FlaggedOnlySwitch.IsOn = true;
            UserIdTextBox.Text = incident.UserId.ToString();
            SelectChatFilterItem(incident.ChatId);
            ScheduleFilterApply();
            SetStatus($"Применен фокус: chat_id={incident.ChatId}, user_id={incident.UserId}, flagged-only.");
        }

        private void SetStatus(string message)
        {
            StatusTextBlock.Text = message;
        }

        private void UpdateSummary()
        {
            var total = _allItems.Count;
            var visible = FilteredItems.Count;
            var flagged = _allItems.Count(item => item.Flagged);
            var incidents = IncidentItems.Count;
            SummaryTextBlock.Text =
                $"{visible}/{total} записей • flagged: {flagged} • инцидентов: {incidents} • пресет: {GetPresetTitle(_datePreset)}";
        }

        private static bool Contains(string? source, string value)
        {
            return !string.IsNullOrWhiteSpace(source) &&
                   source.Contains(value, StringComparison.OrdinalIgnoreCase);
        }

        private static string GetPresetTitle(DatePreset preset)
        {
            return preset switch
            {
                DatePreset.Today => "сегодня",
                DatePreset.LastHour => "последний час",
                _ => "все время"
            };
        }

        private static string EscapeCsv(string? value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            var escaped = value.Replace("\"", "\"\"");
            var hasSpecial = escaped.Contains(',') || escaped.Contains('"') || escaped.Contains('\n') || escaped.Contains('\r');
            return hasSpecial ? $"\"{escaped}\"" : escaped;
        }

        private static string PrettyJson(string? rawJson)
        {
            if (string.IsNullOrWhiteSpace(rawJson))
            {
                return "(Raw JSON недоступен для этой записи)";
            }

            try
            {
                using var doc = JsonDocument.Parse(rawJson);
                return JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions { WriteIndented = true });
            }
            catch
            {
                return rawJson;
            }
        }

        private static string BuildSignature(LogRecordView item)
        {
            return string.Join(
                "|",
                item.SourceKind,
                item.Timestamp?.UtcDateTime.ToString("O") ?? string.Empty,
                item.ChatId?.ToString() ?? string.Empty,
                item.UserId?.ToString() ?? string.Empty,
                item.MessageId?.ToString() ?? string.Empty,
                item.DisplayLabel,
                item.Text ?? string.Empty);
        }

        private static void InitializePicker(object picker)
        {
            var hwnd = App.MainWindowHandle;
            if (hwnd != IntPtr.Zero)
            {
                WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            }
        }

        private enum DatePreset
        {
            All = 0,
            Today = 1,
            LastHour = 2
        }

        private readonly record struct IncidentSelectionState(long ChatId, long UserId);

        public sealed class FileSummaryItem
        {
            public string Name { get; set; } = string.Empty;
            public string FullPath { get; set; } = string.Empty;
            public int AddedCount { get; set; }
            public int ParsedCount { get; set; }
            public int SkippedCount { get; set; }
            public int ErrorCount { get; set; }
            public string? ErrorMessage { get; set; }
            public string CountText
            {
                get
                {
                    var message = string.IsNullOrWhiteSpace(ErrorMessage)
                        ? string.Empty
                        : $" • note: {ErrorMessage}";
                    return
                        $"{AddedCount} новых • parsed={ParsedCount} • skipped={SkippedCount} • errors={ErrorCount} • {FullPath}{message}";
                }
            }
            public override string ToString() => CountText;
        }

        public sealed class IncidentSummaryItem
        {
            public long ChatId { get; set; }
            public long UserId { get; set; }
            public string ChatDisplay { get; set; } = string.Empty;
            public string UserDisplay { get; set; } = string.Empty;
            public string LabelSummary { get; set; } = string.Empty;
            public int Count { get; set; }
            public double? MaxConfidence { get; set; }
            public DateTimeOffset LastSeen { get; set; }

            public string Title => $"{UserDisplay} ({UserId})";
            public string Meta
            {
                get
                {
                    var confidence = MaxConfidence.HasValue ? $"{MaxConfidence.Value:P0}" : "n/a";
                    var date = LastSeen == DateTimeOffset.MinValue ? "n/a" : LastSeen.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss");
                    return $"Чат: {ChatDisplay} ({ChatId}) • метки: {LabelSummary} • сообщений: {Count} • max conf: {confidence} • последнее: {date}";
                }
            }

            public override string ToString() => $"{Title} • {Meta}";
        }

        public sealed class ChatFilterItem
        {
            public long? ChatId { get; set; }
            public string Name { get; set; } = string.Empty;
            public override string ToString() => Name;
        }

        public sealed class LabelFilterItem
        {
            public string Label { get; set; } = string.Empty;
            public bool IsAll { get; set; }
            public override string ToString() => Label;
        }
    }

    internal static class DispatcherQueueExtensions
    {
        public static Task EnqueueAsync(this DispatcherQueue queue, Action action)
        {
            var tcs = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
            queue.TryEnqueue(() =>
            {
                try
                {
                    action();
                    tcs.TrySetResult(null);
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            });

            return tcs.Task;
        }
    }
}
