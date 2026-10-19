using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace Timetable.Editor;

public sealed partial class MainWindow
{
    private DesktopSettings settings = null!;
    private double savedBackgroundOpacity = DesktopSettings.DefaultBackgroundOpacity;
    private bool settingsReady;
    private bool SettingsDirty => settingsReady && Math.Abs(BackgroundOpacitySlider.Value - savedBackgroundOpacity) > .000001;
    public double BackgroundOpacityPreview => BackgroundOpacitySlider.Value;
    public event Action<double>? BackgroundOpacityPreviewChanged;

    private void InitializeSettings()
    {
        settings = new DesktopSettings(directory);
        try { savedBackgroundOpacity = settings.ReadBackgroundOpacity(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        { ShowNotice("无法读取外观设置", error.Message, InfoBarSeverity.Warning); }
        BackgroundOpacitySlider.Value = savedBackgroundOpacity;
        settingsReady = true;
        Closed += (_, _) => BackgroundOpacityPreviewChanged?.Invoke(savedBackgroundOpacity);
        UpdateSettingsState();
    }

    private void UpdateSettingsState()
    {
        if (!settingsReady) return;
        BackgroundOpacityValue.Text = BackgroundOpacitySlider.Value.ToString("0.#") + "%";
        SaveSettingsButton.IsEnabled = SettingsDirty;
        SettingsStatus.Text = SettingsDirty ? "正在预览 · 尚未保存" : "设置已保存";
        SaveButton.Visibility = Tabs.SelectedIndex == 2 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void BackgroundOpacityChanged(object sender, RangeBaseValueChangedEventArgs args)
    {
        if (!settingsReady) return;
        BackgroundOpacityPreviewChanged?.Invoke(args.NewValue);
        Changed();
    }

    private void EditorTabChanged(object sender, SelectionChangedEventArgs args)
    {
        if (settingsReady) Changed();
    }

    private void ResetBackgroundOpacity(object sender, RoutedEventArgs args) =>
        BackgroundOpacitySlider.Value = DesktopSettings.DefaultBackgroundOpacity;

    private bool SaveAppearance()
    {
        try
        {
            settings.SaveBackgroundOpacity(BackgroundOpacitySlider.Value);
            savedBackgroundOpacity = BackgroundOpacitySlider.Value;
            Changed();
            Notice.IsOpen = false;
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException)
        {
            ShowNotice("设置尚未保存", error.Message + "。修改仍保留在预览中。", InfoBarSeverity.Error);
            return false;
        }
    }

    private void SaveSettingsClicked(object sender, RoutedEventArgs args) => SaveAppearance();

    private async Task<bool> SavePendingChanges() =>
        (!ScheduleDirty || await Save()) && (!SettingsDirty || SaveAppearance());
}
