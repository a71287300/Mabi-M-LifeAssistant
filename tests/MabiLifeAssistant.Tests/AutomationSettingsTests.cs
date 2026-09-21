using System.IO;

namespace MabiLifeAssistant.Tests;

public sealed class AutomationSettingsTests
{
    [Fact]
    public void Normalize_restores_safe_defaults_and_prevents_duplicate_hotkeys()
    {
        var settings = new MabiLifeAssistant.AutomationSettings
        {
            SettingsVersion = 1,
            SelectedSkillIndex = 99,
            StartHotkeyVirtualKey = 0x1B,
            StopHotkeyVirtualKey = 0x21,
            RequireInputStability = false
        };

        var normalized = MabiLifeAssistant.AutomationSettingsStore.Normalize(settings);

        Assert.Equal(MabiLifeAssistant.AutomationSettingsStore.CurrentVersion, normalized.SettingsVersion);
        Assert.Equal(0, normalized.SelectedSkillIndex);
        Assert.Equal(MabiLifeAssistant.AutomationSettingsStore.DefaultStartHotkey, normalized.StartHotkeyVirtualKey);
        Assert.NotEqual(normalized.StartHotkeyVirtualKey, normalized.StopHotkeyVirtualKey);
        Assert.True(normalized.RequireInputStability);
    }

    [Fact]
    public void Save_and_load_round_trip_user_preferences()
    {
        var path = Path.Combine(Path.GetTempPath(), "MabiLifeAssistantTests", Guid.NewGuid().ToString("N"), "settings.json");
        try
        {
            var settings = new MabiLifeAssistant.AutomationSettings
            {
                SelectedSkillIndex = 6,
                StartHotkeyVirtualKey = 0x70,
                StopHotkeyVirtualKey = 0x71,
                RequireInputStability = false
            };

            Assert.True(MabiLifeAssistant.AutomationSettingsStore.Save(settings, settings.SelectedSkillIndex, path));
            var loaded = MabiLifeAssistant.AutomationSettingsStore.Load(path);

            Assert.Equal(6, loaded.SelectedSkillIndex);
            Assert.Equal(0x70, loaded.StartHotkeyVirtualKey);
            Assert.Equal(0x71, loaded.StopHotkeyVirtualKey);
            Assert.False(loaded.RequireInputStability);
        }
        finally
        {
            var directory = Directory.GetParent(path)?.Parent?.FullName;
            if (directory is not null && Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }
}
