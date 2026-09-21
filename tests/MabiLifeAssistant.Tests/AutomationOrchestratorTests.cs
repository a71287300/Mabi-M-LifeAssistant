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
            activityMonitor: null!,
            openGuideAsync: null!,
            hasUserActedSince: null!,
            setStage: null!,
            setStatus: null!,
            reportRecoverableError: null!));
    }
}
