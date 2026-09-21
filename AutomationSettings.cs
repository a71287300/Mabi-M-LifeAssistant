using System.Text.Json;
using System.IO;

namespace MabiLifeAssistant;

internal sealed class AutomationSettings
{
    public int SettingsVersion { get; set; } = AutomationSettingsStore.CurrentVersion;
    public int SelectedSkillIndex { get; set; }
    public int StartHotkeyVirtualKey { get; set; } = AutomationSettingsStore.DefaultStartHotkey;
    public int StopHotkeyVirtualKey { get; set; } = AutomationSettingsStore.DefaultStopHotkey;
    public bool RequireInputStability { get; set; } = true;
}

internal static class AutomationSettingsStore
{
    public const int CurrentVersion = 3;
    public const int DefaultStartHotkey = 0x21;
    public const int DefaultStopHotkey = 0x22;
    private const int VirtualKeyEscape = 0x1B;

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "MabiLifeAssistant", "settings.json");

    public static AutomationSettings Load(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            if (File.Exists(path))
            {
                var settings = JsonSerializer.Deserialize<AutomationSettings>(File.ReadAllText(path));
                if (settings is not null)
                    return Normalize(settings);
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // A broken settings file must never prevent the assistant from opening.
        }

        return Normalize(new AutomationSettings());
    }

    public static bool Save(AutomationSettings settings, int selectedSkillIndex, string? path = null)
    {
        path ??= DefaultPath;
        settings.SelectedSkillIndex = Math.Clamp(selectedSkillIndex, 0, SkillCatalog.Names.Length - 1);
        Normalize(settings);
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);
            File.WriteAllText(path, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static AutomationSettings Normalize(AutomationSettings settings)
    {
        if (settings.SettingsVersion < 2)
        {
            // Older builds saved the insect skill used during testing.
            settings.SelectedSkillIndex = 0;
            settings.SettingsVersion = 2;
        }

        if (settings.SettingsVersion < 3)
        {
            settings.StartHotkeyVirtualKey = DefaultStartHotkey;
            settings.StopHotkeyVirtualKey = DefaultStopHotkey;
            settings.RequireInputStability = true;
            settings.SettingsVersion = CurrentVersion;
        }

        settings.SettingsVersion = CurrentVersion;
        settings.SelectedSkillIndex = Math.Clamp(settings.SelectedSkillIndex, 0, SkillCatalog.Names.Length - 1);
        settings.StartHotkeyVirtualKey = IsUsableHotkey(settings.StartHotkeyVirtualKey)
            ? settings.StartHotkeyVirtualKey
            : DefaultStartHotkey;
        settings.StopHotkeyVirtualKey = IsUsableHotkey(settings.StopHotkeyVirtualKey)
            ? settings.StopHotkeyVirtualKey
            : DefaultStopHotkey;

        if (settings.StartHotkeyVirtualKey == settings.StopHotkeyVirtualKey)
        {
            settings.StopHotkeyVirtualKey = settings.StartHotkeyVirtualKey == DefaultStopHotkey
                ? DefaultStartHotkey
                : DefaultStopHotkey;
            if (settings.StartHotkeyVirtualKey == settings.StopHotkeyVirtualKey)
                settings.StartHotkeyVirtualKey = DefaultStartHotkey;
        }

        return settings;
    }

    public static bool IsUsableHotkey(int virtualKey) => virtualKey is > 0 and <= 0xFE
        && virtualKey != VirtualKeyEscape;
}
