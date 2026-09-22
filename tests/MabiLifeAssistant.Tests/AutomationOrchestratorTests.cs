namespace MabiLifeAssistant.Tests;

public sealed class AutomationOrchestratorTests
{
    [Fact]
    public void Rejects_a_skill_index_outside_the_supported_catalog()
    {
        var target = new MabiLifeAssistant.WindowChoice();

        Assert.Throws<ArgumentOutOfRangeException>(() => new MabiLifeAssistant.AutomationOrchestrator(
            target,
            MabiLifeAssistant.SkillCatalog.Names.Length,
            requireInputStability: true,
            getLastUserInputMilliseconds: null!,
            openGuideAsync: null!,
            hasUserActedSince: null!,
            setStage: null!,
            setStatus: null!,
            reportRecoverableError: null!));
    }

    [Fact]
    public async Task Stops_before_interacting_when_window_is_unusable()
    {
        var orchestrator = new MabiLifeAssistant.AutomationOrchestrator(
            new MabiLifeAssistant.WindowChoice { Handle = new IntPtr(1) },
            selectedSkillIndex: 0,
            requireInputStability: false,
            getLastUserInputMilliseconds: () => Environment.TickCount64,
            openGuideAsync: (_, _, _) => Task.FromResult<IReadOnlyList<MabiLifeAssistant.RecognizedLine>?>(Array.Empty<MabiLifeAssistant.RecognizedLine>()),
            hasUserActedSince: _ => false,
            setStage: _ => { },
            setStatus: (_, _, _) => { },
            reportRecoverableError: _ => { },
            windowService: new FakeGameWindowService());

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => orchestrator.RunAsync(CancellationToken.None));

        Assert.Contains("關閉或最小化", exception.Message);
    }

    private sealed class FakeGameWindowService : MabiLifeAssistant.IGameWindowService
    {
        public bool IsUsable(IntPtr handle) => false;

        public System.Windows.Media.Imaging.BitmapSource CaptureClient(IntPtr handle) =>
            throw new NotSupportedException();

        public bool Focus(IntPtr handle) => true;

        public void Click(IntPtr handle, System.Windows.Point point) =>
            throw new NotSupportedException();

        public IReadOnlyList<System.Windows.Point?> FindProceedButtons(System.Windows.Media.Imaging.BitmapSource source) =>
            Array.Empty<System.Windows.Point?>();

        public System.Windows.Point? FindConfirmationButton(System.Windows.Media.Imaging.BitmapSource source) => null;
    }
}
