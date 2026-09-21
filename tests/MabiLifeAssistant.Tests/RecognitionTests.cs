namespace MabiLifeAssistant.Tests;

public sealed class RecognitionTests
{
    [Fact]
    public void Ocr_normalization_removes_punctuation_and_spaces()
    {
        var normalized = MabiLifeAssistant.ScreenTextRecognizer.Normalize(" 生活力： 9,864 ");

        Assert.Equal("生活力9864", normalized);
    }

    [Fact]
    public void Skill_catalog_contains_exactly_the_supported_eight_skills()
    {
        Assert.Equal(8, MabiLifeAssistant.SkillCatalog.Names.Length);
        Assert.Contains("採礦", MabiLifeAssistant.SkillCatalog.Names);
        Assert.Contains("昆蟲採集", MabiLifeAssistant.SkillCatalog.Names);
    }

    [Fact]
    public void Embedded_work_state_model_can_be_loaded()
    {
        var classifier = MabiLifeAssistant.WorkStateClassifier.Create();

        Assert.NotNull(classifier);
    }
}
