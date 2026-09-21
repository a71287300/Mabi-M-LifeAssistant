using System.Windows.Media.Imaging;

namespace MabiLifeAssistant;

internal sealed class AutomationOrchestrator
{
    public const int StabilitySeconds = 5;

    private readonly WindowChoice _target;
    private readonly int _selectedSkillIndex;
    private readonly string _skillName;
    private readonly bool _requireInputStability;
    private readonly UserActivityMonitor _activityMonitor;
    private readonly Func<WindowChoice, CancellationToken, Func<bool>?, Task<IReadOnlyList<RecognizedLine>?>> _openGuideAsync;
    private readonly Func<long, bool> _hasUserActedSince;
    private readonly Action<string> _setStage;
    private readonly Action<string, string, string> _setStatus;
    private readonly Action<Exception> _reportRecoverableError;

    public AutomationOrchestrator(
        WindowChoice target,
        int selectedSkillIndex,
        bool requireInputStability,
        UserActivityMonitor activityMonitor,
        Func<WindowChoice, CancellationToken, Func<bool>?, Task<IReadOnlyList<RecognizedLine>?>> openGuideAsync,
        Func<long, bool> hasUserActedSince,
        Action<string> setStage,
        Action<string, string, string> setStatus,
        Action<Exception> reportRecoverableError)
    {
        if (!SkillCatalog.IsValidIndex(selectedSkillIndex))
            throw new ArgumentOutOfRangeException(nameof(selectedSkillIndex));

        _target = target;
        _selectedSkillIndex = selectedSkillIndex;
        _skillName = SkillCatalog.Names[selectedSkillIndex];
        _requireInputStability = requireInputStability;
        _activityMonitor = activityMonitor;
        _openGuideAsync = openGuideAsync;
        _hasUserActedSince = hasUserActedSince;
        _setStage = setStage;
        _setStatus = setStatus;
        _reportRecoverableError = reportRecoverableError;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var lastInputSeen = _activityMonitor.LastUserInputMilliseconds;
        var lastUiUpdate = 0L;
        var motionDetector = new GameMotionDetector();
        var workStateClassifier = WorkStateClassifier.Create();
        var lastMotionAt = Environment.TickCount64;
        var lastMotionSampleAt = 0L;
        var roundTracker = new WorkStateRoundTracker();

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _setStage("檢查遊戲視窗");
            if (!GameWindowService.IsUsable(_target.Handle))
                throw new InvalidOperationException("遊戲視窗已關閉或最小化。請重新選取視窗後再開始。");

            var now = Environment.TickCount64;
            var lastInput = _activityMonitor.LastUserInputMilliseconds;
            if (now - lastMotionSampleAt >= 900)
            {
                _setStage("搜尋遊戲畫面中的指南針或工作圖案");
                var frame = await Task.Run(() => GameWindowService.CaptureClient(_target.Handle), cancellationToken);
                var workPrediction = workStateClassifier.PredictAnywhere(frame);
                var observedState = workPrediction.State == WorkState.Unknown && motionDetector.IsWorkIndicatorActive(frame, workPrediction.Bounds)
                    ? WorkState.Working
                    : workPrediction.State;
                if (observedState == WorkState.Working)
                {
                    motionDetector.Reset();
                }
                else if (motionDetector.HasSignificantMotion(frame, workPrediction.Bounds))
                {
                    lastMotionAt = Environment.TickCount64;
                }

                // The stop square can be visible for fewer than three samples,
                // so the five-frame vote may never become WorkIndicatorActive.
                // Remember any confirmed working sample for this submitted
                // round instead of relying only on the debounced UI state.
                // A round must first show the in-game working indicator. Once
                // that indicator has disappeared and two consecutive idle
                // frames have arrived, release the round gate and start a
                // fresh stability timer for the next round.
                if (roundTracker.Observe(observedState))
                {
                    lastMotionAt = Environment.TickCount64;
                    motionDetector.Reset();
                }
                lastMotionSampleAt = Environment.TickCount64;
            }

            if (roundTracker.WorkIndicatorActive)
            {
                if (now - lastUiUpdate > 900)
                {
                    _setStatus("工作中", "已找到綠色圓形與白色停止方塊，暫停辨識與點擊；圖案消失後再等待停止。按 Esc 暫停。", "#67D99B");
                    lastUiUpdate = now;
                }

                await Task.Delay(250, cancellationToken);
                continue;
            }

            if (roundTracker.WorkStateUnknown)
            {
                if (now - lastUiUpdate > 900)
                {
                    _setStatus("確認中", "尚未連續找到可確認的指南針或工作圖案，暫停辨識與點擊以避免誤操作。按 Esc 暫停。", "#E8C78B");
                    lastUiUpdate = now;
                }

                await Task.Delay(250, cancellationToken);
                continue;
            }

            if (roundTracker.WaitingForRoundActivity)
            {
                if (now - lastUiUpdate > 900)
                {
                    var workSeenText = roundTracker.RoundWorkSeen ? "已確認工作圖案出現" : "等待確認工作圖案出現";
                    _setStatus("等待完成", $"{workSeenText}；工作圖案消失並確認停止後，等待指南針穩定 {StabilitySeconds} 秒自動開始下一輪。按 Esc 暫停。", "#E8C78B");
                    lastUiUpdate = now;
                }

                await Task.Delay(250, cancellationToken);
                continue;
            }

            var inputIdleFor = Math.Max(0, now - lastInput);
            var visualIdleFor = Math.Max(0, now - lastMotionAt);
            var idleFor = _requireInputStability
                ? Math.Min(inputIdleFor, visualIdleFor)
                : visualIdleFor;
            var idleRequired = StabilitySeconds * 1000L;

            if (now - lastUiUpdate > 900)
            {
                var idleRemaining = Math.Max(0, idleRequired - idleFor);
                var waitingSeconds = (int)Math.Ceiling(idleRemaining / 1000d);
                var inputStableSeconds = (int)Math.Min(StabilitySeconds, inputIdleFor / 1000d);
                var visualStableSeconds = (int)Math.Min(StabilitySeconds, visualIdleFor / 1000d);
                var runTitle = waitingSeconds > 0 ? "閒置中" : "準備開始";
                var runDetail = waitingSeconds > 0
                    ? _requireInputStability
                        ? $"輸入穩定 {inputStableSeconds}/{StabilitySeconds} 秒；指南針穩定 {visualStableSeconds}/{StabilitySeconds} 秒；還需約 {waitingSeconds} 秒。按 Esc 暫停。"
                        : $"輸入穩定等待已關閉；指南針穩定 {visualStableSeconds}/{StabilitySeconds} 秒；還需約 {waitingSeconds} 秒。按 Esc 暫停。"
                    : _requireInputStability
                        ? $"輸入與找到的指南針已穩定 {StabilitySeconds} 秒，即將執行「{_skillName}」。按 Esc 暫停。"
                        : $"找到的指南針已穩定 {StabilitySeconds} 秒，即將執行「{_skillName}」。按 Esc 暫停。";
                _setStatus(runTitle, runDetail, "#A6E4C1");
                lastUiUpdate = now;
            }

            if (idleFor < idleRequired)
            {
                await Task.Delay(250, cancellationToken);
                continue;
            }

            lastInputSeen = _activityMonitor.LastUserInputMilliseconds;
            _setStage("切換至指定遊戲視窗");
            if (!GameWindowService.Focus(_target.Handle))
            {
                _reportRecoverableError(new InvalidOperationException("指定視窗目前無法取得前景焦點；程式沒有送出任何遊戲操作。"));
                await Task.Delay(1000, cancellationToken);
                continue;
            }

            await Task.Delay(250, cancellationToken);
            if (_hasUserActedSince(lastInputSeen))
                continue;

            _setStage("辨識生活力指南");
            _setStatus("辨識中", "只分析選取視窗；先確認生活力指南，再定位所選技能。按 Esc 可停止。", "#A6D7E4");
            var lines = await _openGuideAsync(_target, cancellationToken, () => _hasUserActedSince(lastInputSeen));
            if (lines is null || _hasUserActedSince(lastInputSeen))
                continue;

            if (_hasUserActedSince(lastInputSeen))
                continue;

            _setStage("定位所選採集項目");
            var skillFrame = await Task.Run(() => GameWindowService.CaptureClient(_target.Handle), cancellationToken);
            var proceedButtons = GameWindowService.FindProceedButtons(skillFrame);
            var proceedButton = _selectedSkillIndex < proceedButtons.Count
                ? proceedButtons[_selectedSkillIndex]
                : null;
            if (proceedButton is null)
                throw new InvalidOperationException($"生活力指南已確認，但找不到「{_skillName}」卡片裡的綠色進行按鈕；已停止。");
            if (_hasUserActedSince(lastInputSeen))
                continue;

            _setStage("點擊所選採集項目的進行按鈕");
            GameWindowService.Click(_target.Handle, proceedButton.Value);
            await Task.Delay(450, cancellationToken);
            if (_hasUserActedSince(lastInputSeen))
                continue;

            _setStage("等待遊戲確認視窗");
            System.Windows.Point? confirmationButton = null;
            for (var attempt = 0; attempt < 3; attempt++)
            {
                await Task.Delay(attempt == 0 ? 250 : 350, cancellationToken);
                var confirmationFrame = await Task.Run(() => GameWindowService.CaptureClient(_target.Handle), cancellationToken);
                confirmationButton = GameWindowService.FindConfirmationButton(confirmationFrame);
                if (confirmationButton is not null)
                    break;
            }

            if (confirmationButton is not { } confirmationPoint)
                throw new InvalidOperationException("點擊採集項目後，連續 3 次擷取都找不到確認按鈕；已停止，沒有進入下一輪等待。");

            _setStage("確認十次採集");
            _setStatus("確認採集", "已定位遊戲確認視窗，正在確認十次採集。按 Esc 可停止。", "#A6D7E4");
            if (_hasUserActedSince(lastInputSeen))
                continue;
            GameWindowService.Click(_target.Handle, confirmationPoint);
            await Task.Delay(450, cancellationToken);

            _setStage("等待遊戲動作完成");
            roundTracker.BeginRound();
            motionDetector.Reset();
            lastMotionAt = Environment.TickCount64;
            lastMotionSampleAt = 0;
            _setStatus("工作中", $"已確認「{_skillName}」並開始遊戲內十次採集；偵測到動作停止後會自動開始下一輪。", "#67D99B");
            lastUiUpdate = Environment.TickCount64;
        }
    }


}
