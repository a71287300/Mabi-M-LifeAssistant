namespace MabiLifeAssistant;

/// <summary>
/// Pure OCR decisions used by the guide workflow. Keeping these rules outside
/// the WPF window makes OCR regressions easy to test with recorded lines.
/// </summary>
internal static class RecognitionRules
{
    public static RecognizedLine? FindLifePowerCardLine(
        IReadOnlyList<RecognizedLine> lines,
        double width,
        double height)
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

    public static string BuildRecognitionSummary(IReadOnlyList<RecognizedLine> lines)
    {
        var summary = string.Join("；", lines
            .OrderBy(line => line.Bounds.Y)
            .ThenBy(line => line.Bounds.X)
            .Take(24)
            .Select(line => $"{line.Text}@{line.Bounds.X:0},{line.Bounds.Y:0}"));
        return string.IsNullOrWhiteSpace(summary) ? "（沒有讀到文字）" : summary;
    }

    public static bool LooksLikeLifePowerLabel(string value)
    {
        var normalized = ScreenTextRecognizer.Normalize(value);
        if (normalized.Contains("生活力", StringComparison.Ordinal))
            return true;

        var likelyLifePrefix = normalized.StartsWith("生", StringComparison.Ordinal)
                               || normalized.StartsWith("和", StringComparison.Ordinal);
        if (!likelyLifePrefix || !normalized.Contains("活", StringComparison.Ordinal))
            return false;

        return normalized.Length <= 4
               || normalized.Contains("力", StringComparison.Ordinal)
               || normalized.Any(char.IsDigit);
    }

    public static bool IsLifeSkillsGuide(IReadOnlyList<RecognizedLine> lines)
    {
        var text = string.Concat(lines.Select(line => ScreenTextRecognizer.Normalize(line.Text)));
        return text.Contains(ScreenTextRecognizer.Normalize("生活力指南"), StringComparison.Ordinal)
               && CountRecognizedSkills(lines) >= 3
               && lines.Count(line => ScreenTextRecognizer.Normalize(line.Text).Contains("Lv", StringComparison.OrdinalIgnoreCase)) >= 4;
    }

    public static int CountRecognizedSkills(IReadOnlyList<RecognizedLine> lines)
    {
        var text = string.Concat(lines.Select(line => ScreenTextRecognizer.Normalize(line.Text)));
        return SkillCatalog.Names.Count(name => text.Contains(ScreenTextRecognizer.Normalize(name), StringComparison.Ordinal));
    }
}
