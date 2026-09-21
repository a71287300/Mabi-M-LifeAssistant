using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WpfRadioButton = System.Windows.Controls.RadioButton;
using WpfColor = System.Windows.Media.Color;
using WpfRectangle = System.Windows.Shapes.Rectangle;
using WpfKeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace MabiLifeAssistant;

public partial class MainWindow : Window
{
    private const int IdleSeconds = AutomationOrchestrator.StabilitySeconds;
    private static string[] SkillNames => SkillCatalog.Names;

    private readonly ObservableCollection<WindowChoice> _windows = new();
    private readonly UserActivityMonitor _activityMonitor;
    private readonly DispatcherTimer _previewTimer;
    private readonly AutomationSettings _settings;
    private CancellationTokenSource? _runCancellation;
    private ScreenTextRecognizer? _recognizer;
    private string? _recognitionUnavailableReason;
    private int _selectedSkillIndex;
    private bool _captureBusy;
    private bool _testingRecognition;
    private bool _closing;
    private HotkeyCaptureTarget _hotkeyCaptureTarget;
    private string _automationStage = "尚未開始";
    private string? _lastDebugDetails;
    private IReadOnlyList<RecognitionMarker> _recognitionMarkers = Array.Empty<RecognitionMarker>();

    public MainWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => ApplyDarkTitleBar();
        WindowComboBox.ItemsSource = _windows;
        _settings = AutomationSettingsStore.Load();
        _selectedSkillIndex = Math.Clamp(_settings.SelectedSkillIndex, 0, SkillNames.Length - 1);
        ((WpfRadioButton)FindName($"Skill{_selectedSkillIndex}")!).IsChecked = true;
        InputStabilityCheckBox.IsChecked = _settings.RequireInputStability;
        UpdateHotkeyLabels();
        SaveSettings(); // Drop old coordinate calibration settings; positions are now found from the selected game's text.

        _activityMonitor = new UserActivityMonitor(OnEmergencyStop);
        _activityMonitor.ConfigureHotkeys(_settings.StartHotkeyVirtualKey, _settings.StopHotkeyVirtualKey);
        _activityMonitor.HotkeyPressed += UserHotkeyPressed;
        _previewTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(750) };
        _previewTimer.Tick += PreviewTimerTick;
        RefreshWindows();
        UpdateRecognitionUi();
        Loaded += async (_, _) =>
        {
            try
            {
                // Initialize RapidOCR on the WPF startup thread so model loading
                // errors are shown before the user starts automation.
                _recognizer = ScreenTextRecognizer.Create();
            }
            catch (Exception ex)
            {
                _recognitionUnavailableReason = ex.Message;
            }
            UpdateRecognitionUi();
            if (SelectedWindow is not null)
                _previewTimer.Start();
            await RefreshPreviewAsync();
        };
    }

    private WindowChoice? SelectedWindow => WindowComboBox.SelectedItem as WindowChoice;
    private bool IsRunning => _runCancellation is not null;

    private void RefreshWindowsClick(object sender, RoutedEventArgs e) => RefreshWindows();

    private void RefreshWindows()
    {
        var previousHandle = SelectedWindow?.Handle ?? IntPtr.Zero;
        var found = GameWindowService.FindVisibleWindows(Process.GetCurrentProcess().Id);
        _windows.Clear();
        foreach (var item in found)
            _windows.Add(item);

        WindowComboBox.SelectedItem = previousHandle != IntPtr.Zero
            ? _windows.FirstOrDefault(x => x.Handle == previousHandle)
            : null;

        if (_windows.Count == 0)
            SetStatus("找不到可用視窗", "請先開啟瑪奇，再按右上方「重新整理」。", "#E8C78B");
        else if (SelectedWindow is null)
            SetStatus("請選取遊戲視窗", "選取視窗後，選好採集項目即可開始。", "#E8C78B");
    }

    private void WindowSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded)
            return;

        _previewTimer.Stop();
        PreviewImage.Source = null;
        PreviewImage.Visibility = Visibility.Collapsed;
        PreviewTag.Visibility = Visibility.Collapsed;
        ClearRecognitionMarkers();
        PreviewPlaceholder.Visibility = SelectedWindow is null ? Visibility.Visible : Visibility.Collapsed;
        if (SelectedWindow is { } target)
        {
            PreviewPlaceholder.Text = "正在擷取視窗…";
            PreviewPlaceholder.Visibility = Visibility.Visible;
            PreviewTag.Visibility = Visibility.Visible;
            _previewTimer.Start();
            _ = RefreshPreviewAsync();
            SetStatus("已選取遊戲視窗", target.Title, "#A6E4C1");
        }
        UpdateRecognitionUi();
    }

    private async void PreviewTimerTick(object? sender, EventArgs e) => await RefreshPreviewAsync();

    private async Task RefreshPreviewAsync()
    {
        var target = SelectedWindow;
        if (target is null || _captureBusy || !GameWindowService.IsUsable(target.Handle))
            return;

        _captureBusy = true;
        try
        {
            var bitmap = await Task.Run(() => GameWindowService.CaptureClient(target.Handle));
            if (_closing || SelectedWindow?.Handle != target.Handle)
                return;

            PreviewImage.Source = bitmap;
            PreviewImage.Visibility = Visibility.Visible;
            PreviewPlaceholder.Visibility = Visibility.Collapsed;
            RenderRecognitionMarkers();
            PreviewTagText.Text = "即時預覽";
            PreviewTagText.Foreground = new SolidColorBrush(WpfColor.FromRgb(223, 233, 225));
        }
        catch (Exception ex)
        {
            if (!_closing && SelectedWindow?.Handle == target.Handle)
            {
                PreviewImage.Visibility = Visibility.Collapsed;
                PreviewPlaceholder.Text = "無法擷取此視窗。請確認遊戲未最小化，並嘗試將遊戲切換為視窗模式。";
                PreviewPlaceholder.Visibility = Visibility.Visible;
                PreviewTagText.Text = "預覽中斷";
                PreviewTagText.Foreground = new SolidColorBrush(WpfColor.FromRgb(232, 199, 139));
                SetErrorStatus("預覽擷取失敗", BuildErrorDetail(ex));
            }
        }
        finally
        {
            _captureBusy = false;
        }
    }

    private void SetRecognitionMarkers(IReadOnlyList<RecognitionMarker> markers)
    {
        _recognitionMarkers = markers;
        RenderRecognitionMarkers();
    }

    private void PreviewOverlaySizeChanged(object sender, SizeChangedEventArgs e) => RenderRecognitionMarkers();

    private void ClearRecognitionMarkers()
    {
        _recognitionMarkers = Array.Empty<RecognitionMarker>();
        if (PreviewOverlay is not null)
            PreviewOverlay.Children.Clear();
    }

    private void RenderRecognitionMarkers()
    {
        if (PreviewOverlay is null)
            return;

        PreviewOverlay.Children.Clear();
        if (PreviewImage.Source is not BitmapSource source ||
            PreviewOverlay.ActualWidth <= 0 || PreviewOverlay.ActualHeight <= 0)
            return;

        var scale = Math.Min(PreviewOverlay.ActualWidth / source.PixelWidth,
            PreviewOverlay.ActualHeight / source.PixelHeight);
        var displayWidth = source.PixelWidth * scale;
        var displayHeight = source.PixelHeight * scale;
        var offsetX = (PreviewOverlay.ActualWidth - displayWidth) / 2;
        var offsetY = (PreviewOverlay.ActualHeight - displayHeight) / 2;

        foreach (var marker in _recognitionMarkers)
        {
            var left = offsetX + marker.Bounds.X * scale;
            var top = offsetY + marker.Bounds.Y * scale;
            var width = Math.Max(2, marker.Bounds.Width * scale);
            var height = Math.Max(2, marker.Bounds.Height * scale);
            var color = (WpfColor)System.Windows.Media.ColorConverter.ConvertFromString(marker.ColorHex);

            var box = new WpfRectangle
            {
                Width = width,
                Height = height,
                Stroke = new SolidColorBrush(color),
                StrokeThickness = 2,
                Fill = new SolidColorBrush(WpfColor.FromArgb(24, color.R, color.G, color.B))
            };
            Canvas.SetLeft(box, left);
            Canvas.SetTop(box, top);
            PreviewOverlay.Children.Add(box);

            var label = new Border
            {
                Background = new SolidColorBrush(WpfColor.FromArgb(225, color.R, color.G, color.B)),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(5, 2, 5, 2),
                Child = new TextBlock
                {
                    Text = marker.Label,
                    Foreground = new SolidColorBrush(WpfColor.FromRgb(12, 25, 18)),
                    FontSize = 10,
                    FontWeight = FontWeights.Bold
                }
            };
            Canvas.SetLeft(label, Math.Max(0, left));
            Canvas.SetTop(label, Math.Max(0, top - 23));
            PreviewOverlay.Children.Add(label);
        }
    }

    private static System.Windows.Rect CreateLifePowerMarker(RecognizedLine line, double width, double height)
    {
        var centerY = line.Bounds.Y + line.Bounds.Height / 2;
        var top = Math.Max(height * 0.16, centerY - height * 0.045);
        var bottom = Math.Min(height * 0.44, centerY + height * 0.045);
        return new System.Windows.Rect(width * 0.065, top, width * 0.31, Math.Max(height * 0.055, bottom - top));
    }

    private void OpenMenuClick(object sender, RoutedEventArgs e)
    {
        if (SelectedWindow is not { } target)
        {
            SetStatus("請先選取遊戲視窗", "選取後可將 C 送到該遊戲視窗。", "#E8C78B");
            return;
        }

        if (!GameWindowService.Focus(target.Handle))
        {
            SetStatus("無法切換至遊戲", "請先確認遊戲視窗沒有最小化，再試一次。", "#E8C78B");
            return;
        }

        try
        {
            GameWindowService.PressC(target.Handle);
            PreviewHelpText.Text = "已在指定遊戲視窗按下 C。接著辨識角色面板中的「生活力」數值卡片。";
            SetStatus("已在遊戲中按下 C", "按「測試辨識」會打開生活力指南並驗證技能清單，不會開始採集。", "#A6E4C1");
        }
        catch (Exception ex)
        {
            SetErrorStatus("無法操作遊戲視窗", BuildErrorDetail(ex));
        }
    }

    private async void TestRecognitionClick(object sender, RoutedEventArgs e)
    {
        if (SelectedWindow is not { } target)
        {
            SetStatus("請先選取遊戲視窗", "辨識與點擊只會作用在清單中選取的遊戲視窗。", "#E8C78B");
            return;
        }
        if (_recognizer is null)
        {
            SetStatus("繁體中文辨識尚未就緒", _recognitionUnavailableReason ?? "Windows OCR 正在初始化，請稍後再試。", "#E8C78B");
            return;
        }

        _testingRecognition = true;
        UpdateRecognitionUi();
        try
        {
            if (!GameWindowService.Focus(target.Handle))
                throw new InvalidOperationException("無法切換至選取的遊戲視窗，請確認它沒有最小化。");

            var inputAtTestStart = _activityMonitor.LastUserInputMilliseconds;
            var lines = await OpenLifeSkillsGuideAsync(target, CancellationToken.None,
                () => HasUserActedSince(inputAtTestStart));
            if (lines is null)
                throw new InvalidOperationException("測試期間偵測到鍵盤或滑鼠操作，已停止辨識。");

            var recognized = CountRecognizedSkills(lines);
            var frame = await Task.Run(() => GameWindowService.CaptureClient(target.Handle));
            var buttonCount = GameWindowService.FindProceedButtons(frame).Count(point => point.HasValue);
            PreviewHelpText.Text = "辨識成功：已在遊戲視窗開啟「生活力指南」。預覽畫面僅供查看。";
            SetStatus("辨識測試完成", $"已確認指南，文字辨識到 {recognized}/8 種技能，找到 {buttonCount}/8 個卡片按鈕；沒有選取採集項目。", "#A6E4C1");
        }
        catch (Exception ex)
        {
            PreviewHelpText.Text = "辨識未通過，沒有選取任何生活技能。";
            SetErrorStatus("辨識測試未通過", BuildErrorDetail(ex));
        }
        finally
        {
            _testingRecognition = false;
            UpdateRecognitionUi();
        }
    }

    private async void RecognizeLifePowerClick(object sender, RoutedEventArgs e)
    {
        if (!TryBeginSingleRecognition(out var target))
            return;

        _testingRecognition = true;
        ClearRecognitionMarkers();
        UpdateRecognitionUi();
        try
        {
            if (!GameWindowService.Focus(target.Handle))
                throw new InvalidOperationException("無法切換至選取的遊戲視窗，請確認它沒有最小化。");

            var inputAtTestStart = _activityMonitor.LastUserInputMilliseconds;
            var lines = await ReadTargetWindowAsync(target, CancellationToken.None);
            var clientSize = GameWindowService.GetClientSize(target.Handle);
            var lifePowerLine = FindLifePowerCardLine(lines, clientSize.Width, clientSize.Height);
            if (lifePowerLine is null)
            {
                GameWindowService.PressC(target.Handle);
                await Task.Delay(2000);
                if (HasUserActedSince(inputAtTestStart))
                    throw new InvalidOperationException("偵測到你的鍵盤或滑鼠操作，已停止辨識。");

                lines = await ReadTargetWindowAsync(target, CancellationToken.None);
                clientSize = GameWindowService.GetClientSize(target.Handle);
                lifePowerLine = FindLifePowerCardLine(lines, clientSize.Width, clientSize.Height);
            }

            if (lifePowerLine is null)
                throw new InvalidOperationException("找不到指定區域的「生活力」卡片，請確認角色面板已開啟。");

            SetRecognitionMarkers(new[]
            {
                new RecognitionMarker("生活力", CreateLifePowerMarker(lifePowerLine, clientSize.Width, clientSize.Height), "#A6E4C1")
            });
            PreviewHelpText.Text = "辨識成功：已在預覽畫面框出「生活力」卡片。";
            SetStatus("生活力辨識成功", "綠色框線標記的是指定遊戲視窗中的生活力卡片。", "#A6E4C1");
            await RefreshPreviewAsync();
        }
        catch (Exception ex)
        {
            ClearRecognitionMarkers();
            PreviewHelpText.Text = "生活力辨識未通過，沒有進行其他操作。";
            SetErrorStatus("生活力辨識未通過", BuildErrorDetail(ex));
        }
        finally
        {
            _testingRecognition = false;
            UpdateRecognitionUi();
        }
    }

    private async void RecognizeSkillClick(object sender, RoutedEventArgs e)
    {
        if (!TryBeginSingleRecognition(out var target))
            return;

        _testingRecognition = true;
        ClearRecognitionMarkers();
        UpdateRecognitionUi();
        try
        {
            if (!GameWindowService.Focus(target.Handle))
                throw new InvalidOperationException("無法切換至選取的遊戲視窗，請確認它沒有最小化。");

            var inputAtTestStart = _activityMonitor.LastUserInputMilliseconds;
            var lines = await OpenLifeSkillsGuideAsync(target, CancellationToken.None,
                () => HasUserActedSince(inputAtTestStart));
            if (lines is null)
                throw new InvalidOperationException("偵測到你的鍵盤或滑鼠操作，已停止辨識。");

            var frame = await Task.Run(() => GameWindowService.CaptureClient(target.Handle));
            var boxes = GameWindowService.FindSkillCardBounds(frame);
            var markers = boxes.Select((box, index) => box is { } value
                ? new RecognitionMarker(SkillNames[index], value, index == _selectedSkillIndex ? "#A6E4C1" : "#78B9FF")
                : null)
                .Where(marker => marker is not null)
                .Cast<RecognitionMarker>()
                .ToArray();
            if (markers.Length != SkillNames.Length)
                throw new InvalidOperationException($"生活力指南已確認，但只找到 {markers.Length}/8 個採集項目；已停止，沒有選取技能。");

            SetRecognitionMarkers(markers);
            PreviewHelpText.Text = "辨識成功：已在預覽畫面框出八個採集項目；綠色框是目前選取項目。";
            SetStatus("採集項目辨識成功", "已框出八個支援項目，這次測試沒有點選任何「進行」。", "#A6E4C1");
            await RefreshPreviewAsync();
        }
        catch (Exception ex)
        {
            ClearRecognitionMarkers();
            PreviewHelpText.Text = "採集項目辨識未通過，沒有選取任何技能。";
            SetErrorStatus("採集項目辨識未通過", BuildErrorDetail(ex));
        }
        finally
        {
            _testingRecognition = false;
            UpdateRecognitionUi();
        }
    }

    private bool TryBeginSingleRecognition(out WindowChoice target)
    {
        target = null!;
        if (SelectedWindow is not { } selected)
        {
            SetStatus("請先選取遊戲視窗", "辨識只會作用在清單中選取的遊戲視窗。", "#E8C78B");
            return false;
        }
        if (_recognizer is null)
        {
            SetStatus("繁體中文辨識尚未就緒", _recognitionUnavailableReason ?? "辨識元件正在初始化，請稍後再試。", "#E8C78B");
            return false;
        }

        target = selected;
        return true;
    }

    private async Task<IReadOnlyList<RecognizedLine>?> OpenLifeSkillsGuideAsync(
        WindowChoice target,
        CancellationToken cancellationToken,
        Func<bool>? shouldStop)
    {
        var lines = await ReadTargetWindowAsync(target, cancellationToken);
        if (await IsLifeSkillsGuideAsync(target, lines, cancellationToken))
            return shouldStop?.Invoke() == true ? null : lines;

        var clientSize = GameWindowService.GetClientSize(target.Handle);
        var lifePowerLine = FindLifePowerCardLine(lines, clientSize.Width, clientSize.Height);
        if (lifePowerLine is null)
        {
            // Full-frame OCR can miss the relatively small left-side stat
            // card. Run a second pass on an enlarged crop before pressing C;
            // this also avoids toggling an already-open character panel.
            var enlargedLines = await ReadLifePowerRegionAsync(target, cancellationToken);
            lifePowerLine = FindLifePowerCardLine(enlargedLines, clientSize.Width, clientSize.Height);
        }

        if (lifePowerLine is null)
        {
            if (shouldStop?.Invoke() == true)
                return null;
            GameWindowService.PressC(target.Handle);
            await Task.Delay(2000, cancellationToken);

            // The panel animation and PrintWindow capture can settle at
            // slightly different times. Retry full OCR and the enlarged crop
            // several times instead of failing on one intermediate frame.
            for (var attempt = 0; attempt < 3 && lifePowerLine is null; attempt++)
            {
                if (attempt > 0)
                    await Task.Delay(650, cancellationToken);
                if (shouldStop?.Invoke() == true)
                    return null;

                lines = await ReadTargetWindowAsync(target, cancellationToken);
                if (await IsLifeSkillsGuideAsync(target, lines, cancellationToken))
                    return shouldStop?.Invoke() == true ? null : lines;

                clientSize = GameWindowService.GetClientSize(target.Handle);
                lifePowerLine = FindLifePowerCardLine(lines, clientSize.Width, clientSize.Height);
                if (lifePowerLine is null)
                {
                    var enlargedLines = await ReadLifePowerRegionAsync(target, cancellationToken);
                    lifePowerLine = FindLifePowerCardLine(enlargedLines, clientSize.Width, clientSize.Height);
                }
            }
        }

        if (lifePowerLine is null)
        {
            throw new InvalidOperationException(
                $"在選取的遊戲視窗無法把「生活力」文字與相鄰數值確認為卡片；已停止，沒有猜測點擊位置。" +
                $" 最後擷取尺寸：{clientSize.Width}×{clientSize.Height}，OCR 文字數：{lines.Count}。" +
                $" 可見文字摘要：{BuildRecognitionSummary(lines)}");
        }
        if (shouldStop?.Invoke() == true)
            return null;

        var clickPoint = new System.Windows.Point(lifePowerLine.Bounds.X + lifePowerLine.Bounds.Width / 2,
            lifePowerLine.Bounds.Y + lifePowerLine.Bounds.Height / 2);
        GameWindowService.Click(target.Handle, clickPoint);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            await Task.Delay(attempt == 0 ? 850 : 500, cancellationToken);
            if (shouldStop?.Invoke() == true)
                return null;

            lines = await ReadTargetWindowAsync(target, cancellationToken);
            if (await IsLifeSkillsGuideAsync(target, lines, cancellationToken))
                return lines;
        }

        throw new InvalidOperationException("點選生活力卡片後，畫面沒有通過生活力指南與技能名稱驗證；已停止，沒有選取技能。");
    }

    private async Task<IReadOnlyList<RecognizedLine>> ReadTargetWindowAsync(WindowChoice target, CancellationToken cancellationToken)
    {
        if (_recognizer is null)
            throw new InvalidOperationException(_recognitionUnavailableReason ?? "繁體中文辨識元件尚未就緒。");
        if (!GameWindowService.IsUsable(target.Handle))
            throw new InvalidOperationException("選取的遊戲視窗已關閉或最小化。");

        var bitmap = await Task.Run(() => GameWindowService.CaptureClient(target.Handle), cancellationToken);
        return await _recognizer.RecognizeAsync(bitmap, cancellationToken);
    }

    private async Task<IReadOnlyList<RecognizedLine>> ReadLifePowerRegionAsync(
        WindowChoice target,
        CancellationToken cancellationToken)
    {
        if (_recognizer is null)
            throw new InvalidOperationException(_recognitionUnavailableReason ?? "繁體中文辨識元件尚未就緒。");

        var frame = await Task.Run(() => GameWindowService.CaptureClient(target.Handle), cancellationToken);
        var cropLeft = Math.Clamp((int)Math.Round(frame.PixelWidth * 0.03), 0, Math.Max(0, frame.PixelWidth - 1));
        var cropTop = Math.Clamp((int)Math.Round(frame.PixelHeight * 0.10), 0, Math.Max(0, frame.PixelHeight - 1));
        var cropWidth = Math.Clamp((int)Math.Round(frame.PixelWidth * 0.55), 1, frame.PixelWidth - cropLeft);
        var cropHeight = Math.Clamp((int)Math.Round(frame.PixelHeight * 0.55), 1, frame.PixelHeight - cropTop);
        var crop = new CroppedBitmap(frame, new Int32Rect(cropLeft, cropTop, cropWidth, cropHeight));
        crop.Freeze();
        var scaled = new TransformedBitmap(crop, new ScaleTransform(2, 2));
        scaled.Freeze();

        var regionLines = await _recognizer.RecognizeAsync(scaled, cancellationToken);
        return regionLines.Select(line => line with
        {
            Bounds = new System.Windows.Rect(
                cropLeft + line.Bounds.X / 2,
                cropTop + line.Bounds.Y / 2,
                line.Bounds.Width / 2,
                line.Bounds.Height / 2)
        }).ToArray();
    }

    private static async Task<bool> IsLifeSkillsGuideAsync(
        WindowChoice target,
        IReadOnlyList<RecognizedLine> lines,
        CancellationToken cancellationToken)
    {
        if (IsLifeSkillsGuide(lines))
            return true;

        // Small Chinese labels can be missed at different game scaling levels. The
        // guide has a distinctive eight-card layout, so use all eight green buttons
        // as a second visual confirmation when OCR is incomplete.
        var frame = await Task.Run(() => GameWindowService.CaptureClient(target.Handle), cancellationToken);
        return GameWindowService.FindProceedButtons(frame).Count(point => point.HasValue) == SkillNames.Length;
    }

    private static RecognizedLine? FindLifePowerCardLine(IReadOnlyList<RecognizedLine> lines, double width, double height)
    {
        var labels = lines.Where(line =>
        {
            var text = ScreenTextRecognizer.Normalize(line.Text);
            return LooksLikeLifePowerLabel(line.Text)
                   && !text.Contains("指南", StringComparison.Ordinal)
                   && !text.Contains("技能", StringComparison.Ordinal)
                   && line.Bounds.X >= width * 0.04
                   && line.Bounds.X <= width * 0.50
                   && line.Bounds.Y >= height * 0.10
                   && line.Bounds.Y <= height * 0.60;
        });

        foreach (var label in labels.OrderBy(line => line.Bounds.Y))
        {
            // The life-power value is a large decorative number and can be
            // omitted by sparse OCR. An exact label in this constrained card
            // region is already enough to identify the clickable card safely.
            if (ScreenTextRecognizer.Normalize(label.Text)
                .Equals(ScreenTextRecognizer.Normalize("生活力"), StringComparison.Ordinal))
                return label;

            if (label.Text.Any(char.IsDigit))
                return label;

            var labelCenterY = label.Bounds.Y + label.Bounds.Height / 2;
            var labelRight = label.Bounds.Right;
            var nearbyValue = lines
                .Where(line => line.Text.Any(char.IsDigit))
                .Where(line => Math.Abs(line.Bounds.Y + line.Bounds.Height / 2 - labelCenterY)
                               <= Math.Max(36, label.Bounds.Height * 1.5))
                .Where(line => line.Bounds.X >= labelRight - 12
                               && line.Bounds.X - labelRight <= 320)
                .OrderBy(line => line.Bounds.X - labelRight)
                .FirstOrDefault();

            if (nearbyValue is not null)
                return label;
        }

        return null;
    }

    private static string BuildRecognitionSummary(IReadOnlyList<RecognizedLine> lines)
    {
        var summary = string.Join("；", lines
            .OrderBy(line => line.Bounds.Y)
            .ThenBy(line => line.Bounds.X)
            .Take(24)
            .Select(line => $"{line.Text}@{line.Bounds.X:0},{line.Bounds.Y:0}"));
        return string.IsNullOrWhiteSpace(summary) ? "（沒有讀到文字）" : summary;
    }

    private static bool LooksLikeLifePowerLabel(string value)
    {
        var normalized = ScreenTextRecognizer.Normalize(value);
        if (normalized.Contains("生活力", StringComparison.Ordinal))
            return true;

        // Tesseract can read the supplied card as "和活10,583": the first
        // character is confused, and the final 力 is absorbed into the value.
        // Keep this fallback narrow: it must look like 生活/和活 and remain in
        // the fixed life-power card region checked by FindLifePowerCardLine.
        var likelyLifePrefix = normalized.StartsWith("生", StringComparison.Ordinal)
                               || normalized.StartsWith("和", StringComparison.Ordinal);
        if (!likelyLifePrefix || !normalized.Contains("活", StringComparison.Ordinal))
            return false;

        return normalized.Length <= 4
               || normalized.Contains("力", StringComparison.Ordinal)
               || normalized.Any(char.IsDigit);
    }

    private static bool IsLifeSkillsGuide(IReadOnlyList<RecognizedLine> lines)
    {
        var text = string.Concat(lines.Select(line => ScreenTextRecognizer.Normalize(line.Text)));
        return text.Contains(ScreenTextRecognizer.Normalize("生活力指南"), StringComparison.Ordinal)
               && CountRecognizedSkills(lines) >= 3
               && lines.Count(line => ScreenTextRecognizer.Normalize(line.Text).Contains("Lv", StringComparison.OrdinalIgnoreCase)) >= 4;
    }

    private static int CountRecognizedSkills(IReadOnlyList<RecognizedLine> lines)
    {
        var text = string.Concat(lines.Select(line => ScreenTextRecognizer.Normalize(line.Text)));
        return SkillNames.Count(name => text.Contains(ScreenTextRecognizer.Normalize(name), StringComparison.Ordinal));
    }

    private void SkillSelectionChanged(object sender, RoutedEventArgs e)
    {
        if (sender is WpfRadioButton { IsChecked: true, Tag: string tag } && int.TryParse(tag, out var index))
        {
            _selectedSkillIndex = index;
            _settings.SelectedSkillIndex = index;
            SaveSettings();
            UpdateRecognitionUi();
        }
    }

    private void InputStabilityChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded)
            return;

        _settings.RequireInputStability = InputStabilityCheckBox.IsChecked == true;
        SaveSettings();
        if (!IsRunning)
        {
            var detail = _settings.RequireInputStability
                ? $"鍵盤與滑鼠停止操作 {IdleSeconds} 秒，且指南針穩定後才會開始。"
                : $"已關閉輸入穩定等待，只會等待指南針穩定 {IdleSeconds} 秒。";
            SetStatus("設定已更新", detail, "#A6E4C1");
        }
    }

    private void EditStartHotkeyClick(object sender, RoutedEventArgs e) => BeginHotkeyCapture(HotkeyCaptureTarget.Start);

    private void EditStopHotkeyClick(object sender, RoutedEventArgs e) => BeginHotkeyCapture(HotkeyCaptureTarget.Stop);

    private void BeginHotkeyCapture(HotkeyCaptureTarget target)
    {
        if (IsRunning)
            return;

        _hotkeyCaptureTarget = target;
        var name = target == HotkeyCaptureTarget.Start ? "開始" : "關閉";
        UpdateRecognitionUi();
        SetStatus("等待設定快捷鍵", $"請按下新的{name}按鍵；按 Esc 可取消設定。", "#E8C78B");
        Focus();
    }

    private void WindowPreviewKeyDown(object sender, WpfKeyEventArgs e)
    {
        if (_hotkeyCaptureTarget == HotkeyCaptureTarget.None)
            return;

        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape)
        {
            CancelHotkeyCapture();
            return;
        }

        var virtualKey = KeyInterop.VirtualKeyFromKey(key);
        if (!IsUsableHotkey(virtualKey))
        {
            SetStatus("按鍵無法使用", "請選擇一般鍵盤按鍵；Esc 保留作為緊急暫停。", "#E8C78B");
            return;
        }

        var otherHotkey = _hotkeyCaptureTarget == HotkeyCaptureTarget.Start
            ? _settings.StopHotkeyVirtualKey
            : _settings.StartHotkeyVirtualKey;
        if (virtualKey == otherHotkey)
        {
            var otherName = _hotkeyCaptureTarget == HotkeyCaptureTarget.Start ? "關閉" : "開始";
            SetStatus("按鍵已被使用", $"這個按鍵目前是「{otherName}」快捷鍵，請換一個按鍵。", "#E8C78B");
            return;
        }

        if (_hotkeyCaptureTarget == HotkeyCaptureTarget.Start)
            _settings.StartHotkeyVirtualKey = virtualKey;
        else
            _settings.StopHotkeyVirtualKey = virtualKey;

        _activityMonitor.ConfigureHotkeys(_settings.StartHotkeyVirtualKey, _settings.StopHotkeyVirtualKey);
        SaveSettings();
        var name = _hotkeyCaptureTarget == HotkeyCaptureTarget.Start ? "開始" : "關閉";
        _hotkeyCaptureTarget = HotkeyCaptureTarget.None;
        UpdateHotkeyLabels();
        UpdateRecognitionUi();
        SetStatus("快捷鍵已更新", $"「{name}」快捷鍵設為 {FormatHotkey(virtualKey)}。", "#A6E4C1");
    }

    private void CancelHotkeyCapture()
    {
        _hotkeyCaptureTarget = HotkeyCaptureTarget.None;
        UpdateRecognitionUi();
        SetStatus("已取消設定", "快捷鍵維持原設定。", "#92A198");
    }

    private void UserHotkeyPressed(uint virtualKey)
    {
        if (_closing)
            return;

        _ = Dispatcher.BeginInvoke(() =>
        {
            if (_hotkeyCaptureTarget != HotkeyCaptureTarget.None)
                return;

            if (virtualKey == (uint)_settings.StopHotkeyVirtualKey)
            {
                if (IsRunning)
                    PauseAutomation();
                return;
            }

            if (virtualKey == (uint)_settings.StartHotkeyVirtualKey && !IsRunning)
            {
                if (CanStart())
                    StartClick(StartButton, new RoutedEventArgs());
                else
                    SetStatus("尚未準備完成", "請先選取遊戲視窗並等待辨識元件就緒。", "#E8C78B");
            }
        });
    }

    private void UpdateHotkeyLabels()
    {
        if (StartHotkeyButton is null || StopHotkeyButton is null)
            return;
        StartHotkeyButton.Content = FormatHotkey(_settings.StartHotkeyVirtualKey);
        StopHotkeyButton.Content = FormatHotkey(_settings.StopHotkeyVirtualKey);
    }

    private static bool IsUsableHotkey(int virtualKey) => AutomationSettingsStore.IsUsableHotkey(virtualKey);

    private static string FormatHotkey(int virtualKey) => virtualKey switch
    {
        0x08 => "Backspace",
        0x09 => "Tab",
        0x0D => "Enter",
        0x20 => "Space",
        0x21 => "PageUp",
        0x22 => "PageDown",
        0x23 => "End",
        0x24 => "Home",
        0x25 => "Left",
        0x26 => "Up",
        0x27 => "Right",
        0x28 => "Down",
        0x2D => "Insert",
        0x2E => "Delete",
        >= 0x30 and <= 0x39 => ((char)virtualKey).ToString(),
        >= 0x41 and <= 0x5A => ((char)virtualKey).ToString(),
        >= 0x70 and <= 0x7B => $"F{virtualKey - 0x6F}",
        _ => KeyInterop.KeyFromVirtualKey(virtualKey).ToString()
    };

    private async void StartClick(object sender, RoutedEventArgs e)
    {
        if (!CanStart())
            return;

        SaveSettings();
        _lastDebugDetails = null;
        CopyDebugButton.IsEnabled = false;
        var cts = new CancellationTokenSource();
        _runCancellation = cts;
        _automationStage = "初始化自動流程";
        SetRunningUi(true);
        var inputWaitText = _settings.RequireInputStability
            ? $"輸入停止後，持續穩定 {IdleSeconds} 秒"
            : "已關閉輸入穩定等待";
        SetStatus("閒置中", $"{inputWaitText}，並確認指南針穩定後會執行「{SkillNames[_selectedSkillIndex]}」。", "#A6E4C1");

        var runFailed = false;
        try
        {
            await RunAutomationAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Pause is a normal end to the loop.
        }
        catch (Exception ex)
        {
            runFailed = true;
            SetErrorStatus("自動操作發生問題", BuildAutomationErrorDetail(ex));
        }
        finally
        {
            if (ReferenceEquals(_runCancellation, cts))
            {
                _runCancellation = null;
                SetRunningUi(false);
                if (!_closing && !runFailed)
                    SetStatus("已暫停", "按「開始自動生活」即可再次啟動。按 Esc 也能緊急暫停。", "#92A198");
            }
            cts.Dispose();
        }
    }

    private async Task RunAutomationAsync(CancellationToken cancellationToken)
    {
        var target = SelectedWindow ?? throw new InvalidOperationException("請先選取遊戲視窗。");
        if (_recognizer is null)
            throw new InvalidOperationException(_recognitionUnavailableReason ?? "繁體中文辨識元件尚未就緒。");

        var orchestrator = new AutomationOrchestrator(
            target,
            _selectedSkillIndex,
            _settings.RequireInputStability,
            _activityMonitor,
            OpenLifeSkillsGuideAsync,
            HasUserActedSince,
            stage => _automationStage = stage,
            SetRunState,
            exception => SetErrorStatus("無法切換至遊戲視窗", BuildAutomationErrorDetail(exception)));

        await orchestrator.RunAsync(cancellationToken);
    }

    private string BuildAutomationErrorDetail(Exception exception)
    {
        var skill = _selectedSkillIndex >= 0 && _selectedSkillIndex < SkillNames.Length
            ? SkillNames[_selectedSkillIndex]
            : "未指定";
        var window = SelectedWindow?.Title ?? "未指定";
        var cause = exception.Message.Trim();
        var suggestion = _automationStage switch
        {
            "搜尋遊戲畫面中的指南針或工作圖案" => "請確認遊戲視窗仍可見、沒有最小化，且指南針或工作圖案沒有被其他視窗遮住。",
            "辨識生活力指南" => "請確認遊戲畫面已穩定，並重新選取遊戲視窗後再試。",
            "定位所選採集項目" => "請確認生活力指南仍顯示八個採集項目，且遊戲視窗比例沒有在執行中改變。",
            "等待遊戲確認視窗" => "請確認點擊進行後確實出現確認視窗；若版面改變，請重新辨識採集項目。",
            "確認十次採集" => "請確認確認按鈕仍在畫面中，並重新開始自動流程。",
            "切換至指定遊戲視窗" => "請將遊戲視窗恢復、保持可見，並重新選取指定視窗。",
            _ => "請重新選取遊戲視窗後再試；若再次發生，保留這段錯誤訊息。"
        };

        var version = typeof(MainWindow).Assembly.GetName().Version?.ToString() ?? "未知";
        var handle = SelectedWindow is { } selected ? $"0x{selected.Handle.ToInt64():X}" : "未指定";
        return $"時間：{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz}\n" +
               $"助手版本：{version}\n" +
               $"階段：{_automationStage}\n技能：{skill}\n" +
               $"指定視窗：{window}\n視窗 Handle：{handle}\n" +
               $"原因：{cause}\n建議：{suggestion}\n\n" +
               $"例外完整堆疊：\n{exception}";
    }

    private string BuildErrorDetail(Exception exception)
    {
        var window = SelectedWindow;
        var version = typeof(MainWindow).Assembly.GetName().Version?.ToString() ?? "未知";
        var handle = window is null ? "未指定" : $"0x{window.Handle.ToInt64():X}";
        return $"時間：{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz}\n" +
               $"助手版本：{version}\n" +
               $"階段：{_automationStage}\n" +
               $"指定視窗：{window?.Title ?? "未指定"}\n" +
               $"視窗 Handle：{handle}\n" +
               $"原因：{exception.Message.Trim()}\n\n" +
               $"例外完整堆疊：\n{exception}";
    }

    private void SetErrorStatus(string title, string detail)
    {
        _lastDebugDetails = $"【{title}】\n{detail}";
        CopyDebugButton.IsEnabled = true;
        SetStatus(title, detail, "#E8C78B");
    }

    private void CopyDebugClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_lastDebugDetails))
            return;

        try
        {
            System.Windows.Clipboard.SetText(_lastDebugDetails);
            RunDetailText.Text = "完整診斷資訊已複製到剪貼簿，可直接貼到訊息中。";
        }
        catch (Exception exception)
        {
            RunDetailText.Text = $"複製診斷失敗：{exception.Message}";
        }
    }

    private bool HasUserActedSince(long timestamp) => _activityMonitor.LastUserInputMilliseconds > timestamp + 5;

    private void PauseClick(object sender, RoutedEventArgs e) => PauseAutomation();

    private void PauseAutomation()
    {
        if (_runCancellation is null)
            return;
        StopButton.IsEnabled = false;
        SetRunState("正在暫停…", "停止後不會再送出新的遊戲操作。", "#92A198");
        _runCancellation.Cancel();
    }

    private void OnEmergencyStop()
    {
        if (_closing || !Dispatcher.CheckAccess())
        {
            if (!_closing)
                Dispatcher.BeginInvoke(PauseAutomation);
            return;
        }
        PauseAutomation();
    }

    private void SetRunningUi(bool running)
    {
        StopButton.IsEnabled = running;
        UpdateRecognitionUi();
    }

    private void UpdateRecognitionUi()
    {
        var busy = IsRunning || _testingRecognition;
        var editingHotkey = _hotkeyCaptureTarget != HotkeyCaptureTarget.None;
        RecognitionStatusText.Text = _recognizer is not null
            ? SelectedWindow is null ? "辨識就緒 · 請先選取遊戲視窗" : "辨識就緒 · 預覽僅供查看"
            : _recognitionUnavailableReason ?? "正在載入繁體中文辨識…";
        SkillTargetHelpText.Text = _recognizer is not null
            ? "先確認指南，再找該卡片的綠色「進行」按鈕"
            : "需要 Windows 繁體中文 OCR 元件";
        TestRecognitionButton.IsEnabled = !busy && SelectedWindow is not null && _recognizer is not null;
        RecognizeLifePowerButton.IsEnabled = !busy && SelectedWindow is not null && _recognizer is not null;
        RecognizeSkillButton.IsEnabled = !busy && SelectedWindow is not null && _recognizer is not null;
        InputStabilityCheckBox.IsEnabled = !busy && !editingHotkey;
        StartHotkeyButton.IsEnabled = !busy && !editingHotkey;
        StopHotkeyButton.IsEnabled = !busy && !editingHotkey;
        StartButton.IsEnabled = !busy && CanStart();
        WindowComboBox.IsEnabled = !busy;
        foreach (var item in GetSkillButtons())
            item.IsEnabled = !busy;
    }

    private bool CanStart() => SelectedWindow is { } target
                               && GameWindowService.IsUsable(target.Handle)
                               && _recognizer is not null;

    private void SetStatus(string title, string detail, string colorHex)
    {
        SetRunState(title, detail, colorHex);
    }

    private void SetRunState(string title, string detail, string colorHex)
    {
        HeaderStatusText.Text = title;
        RunStatusText.Text = title;
        RunDetailText.Text = detail;
        var color = (WpfColor)System.Windows.Media.ColorConverter.ConvertFromString(colorHex);
        var accent = new SolidColorBrush(color);
        var softAccent = new SolidColorBrush(WpfColor.FromArgb(42, color.R, color.G, color.B));
        var borderAccent = new SolidColorBrush(WpfColor.FromArgb(150, color.R, color.G, color.B));
        RunStateCard.BorderBrush = borderAccent;
        RunStateCard.Background = softAccent;
        RunStateIconFrame.Background = softAccent;
        RunStateIconFrame.BorderBrush = borderAccent;
        RunStateIcon.Foreground = accent;
        RunStatusText.Foreground = accent;
        StatusDot.Fill = accent;
        RunStateIcon.Text = title.Contains("工作", StringComparison.Ordinal)
            ? "●"
            : title.Contains("閒置", StringComparison.Ordinal) || title.Contains("準備", StringComparison.Ordinal)
                ? "○"
                : title.Contains("暫停", StringComparison.Ordinal)
                    ? "Ⅱ"
                    : title.Contains("確認", StringComparison.Ordinal) || title.Contains("等待", StringComparison.Ordinal)
                        ? "◌"
                        : "•";
    }

    private void SaveSettings()
    {
        _ = AutomationSettingsStore.Save(_settings, _selectedSkillIndex);
    }

    private IEnumerable<WpfRadioButton> GetSkillButtons() => Enumerable.Range(0, SkillNames.Length)
        .Select(i => (WpfRadioButton)FindName($"Skill{i}")!);

    private void WindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _closing = true;
        _runCancellation?.Cancel();
        _previewTimer.Stop();
        _activityMonitor.HotkeyPressed -= UserHotkeyPressed;
        _activityMonitor.Dispose();
        _recognizer?.Dispose();
        SaveSettings();
    }

    private void ApplyDarkTitleBar()
    {
        var handle = new WindowInteropHelper(this).Handle;
        uint captionColor = 0x00141711;
        uint textColor = 0x00F1F5F1;
        uint borderColor = 0x0032392C;
        var darkMode = 1;
        if (DwmSetWindowAttribute(handle, 35, ref captionColor, sizeof(uint)) < 0)
            _ = DwmSetWindowAttribute(handle, 20, ref darkMode, sizeof(int));
        _ = DwmSetWindowAttribute(handle, 36, ref textColor, sizeof(uint));
        _ = DwmSetWindowAttribute(handle, 34, ref borderColor, sizeof(uint));
    }

    private sealed record RecognitionMarker(string Label, System.Windows.Rect Bounds, string ColorHex);

    private enum HotkeyCaptureTarget
    {
        None,
        Start,
        Stop
    }

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr handle, uint attribute, ref uint value, uint valueSize);

    [System.Runtime.InteropServices.DllImport("dwmapi.dll", EntryPoint = "DwmSetWindowAttribute")]
    private static extern int DwmSetWindowAttribute(IntPtr handle, uint attribute, ref int value, uint valueSize);

}

internal sealed class WindowChoice
{
    public IntPtr Handle { get; init; }
    public string Title { get; init; } = string.Empty;
    public string ProcessName { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
}
