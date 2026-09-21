namespace MabiLifeAssistant;

internal static class SkillCatalog
{
    public static readonly string[] Names =
    {
        "日常採集", "採礦", "伐木", "剪羊毛", "鋤地", "收割", "採集藥草", "昆蟲採集"
    };

    public static bool IsValidIndex(int index) => index >= 0 && index < Names.Length;
}
