using System.Windows;

namespace MabiLifeAssistant.Tests;

public sealed class RecognitionRulesTests
{
    [Fact]
    public void Finds_an_exact_life_power_label_inside_the_stat_card_region()
    {
        var lines = new[]
        {
            new MabiLifeAssistant.RecognizedLine("生活力", new Rect(120, 220, 96, 34))
        };

        var result = MabiLifeAssistant.RecognitionRules.FindLifePowerCardLine(lines, 1000, 800);

        Assert.NotNull(result);
        Assert.Equal("生活力", result!.Text);
    }

    [Fact]
    public void Accepts_a_common_ocr_variant_when_the_value_is_adjacent()
    {
        var lines = new[]
        {
            new MabiLifeAssistant.RecognizedLine("和活", new Rect(120, 220, 72, 34)),
            new MabiLifeAssistant.RecognizedLine("10,583", new Rect(220, 216, 120, 46))
        };

        var result = MabiLifeAssistant.RecognitionRules.FindLifePowerCardLine(lines, 1000, 800);

        Assert.NotNull(result);
        Assert.Equal("和活", result!.Text);
    }

    [Fact]
    public void Rejects_a_life_power_label_outside_the_expected_card_region()
    {
        var lines = new[]
        {
            new MabiLifeAssistant.RecognizedLine("生活力", new Rect(700, 220, 96, 34))
        };

        var result = MabiLifeAssistant.RecognitionRules.FindLifePowerCardLine(lines, 1000, 800);

        Assert.Null(result);
    }

    [Fact]
    public void Recognizes_the_guide_from_title_skills_and_levels()
    {
        var lines = new[]
        {
            Line("生活力指南"),
            Line("日常採集"),
            Line("採礦"),
            Line("伐木"),
            Line("Lv.17"),
            Line("Lv.16"),
            Line("Lv.19"),
            Line("Lv.22")
        };

        Assert.True(MabiLifeAssistant.RecognitionRules.IsLifeSkillsGuide(lines));
        Assert.Equal(3, MabiLifeAssistant.RecognitionRules.CountRecognizedSkills(lines));
    }

    [Fact]
    public void Builds_a_debug_summary_for_empty_ocr_results()
    {
        Assert.Equal(
            "（沒有讀到文字）",
            MabiLifeAssistant.RecognitionRules.BuildRecognitionSummary(Array.Empty<MabiLifeAssistant.RecognizedLine>()));
    }

    private static MabiLifeAssistant.RecognizedLine Line(string text) =>
        new(text, new Rect(100, 100, 80, 24));
}
