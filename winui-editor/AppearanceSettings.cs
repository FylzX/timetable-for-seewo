using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using System.Collections.ObjectModel;

namespace Timetable.Editor;

public sealed partial class MainWindow
{
    private DesktopSettings settings = null!;
    private double savedBackgroundOpacity = DesktopSettings.DefaultBackgroundOpacity;
    private string[] savedContextMenuOrder = [];
    private string[] savedHiddenMenuItems = [];
    private bool savedUpdateBadgeEnabled = true;
    private bool updateBadgeEnabled = true;
    private readonly ObservableCollection<MenuOption> contextMenuOptions = [];
    private int selectedTabIndex;
    private bool menuDragStartedOnHandle;
    private bool settingsReady;
    private bool SettingsDirty => settingsReady &&
        (Math.Abs(BackgroundOpacitySlider.Value - savedBackgroundOpacity) > .000001 ||
         !contextMenuOptions.Select(item => item.Id).SequenceEqual(savedContextMenuOrder) ||
         !contextMenuOptions.Where(item => !item.IsEnabled).Select(item => item.Id).Order(StringComparer.Ordinal)
             .SequenceEqual(savedHiddenMenuItems.Order(StringComparer.Ordinal)) ||
         updateBadgeEnabled != savedUpdateBadgeEnabled);
    public double BackgroundOpacityPreview => BackgroundOpacitySlider.Value;
    internal int SelectedTabIndex => selectedTabIndex;
    public event Action<double>? BackgroundOpacityPreviewChanged;
    public event Action<bool>? UpdateBadgePreferenceChanged;

    private void InitializeSettings()
    {
        settings = new DesktopSettings(directory);
        try
        {
            var preferences = settings.ReadPreferences();
            savedBackgroundOpacity = preferences.BackgroundOpacity;
            savedContextMenuOrder = preferences.ContextMenuOrder;
            savedHiddenMenuItems = preferences.HiddenContextMenuItems.Order(StringComparer.Ordinal).ToArray();
            savedUpdateBadgeEnabled = updateBadgeEnabled = preferences.UpdateBadgeEnabled;
            foreach (var id in preferences.ContextMenuOrder)
            {
                var definition = DesktopSettings.ConfigurableMenuItems.Single(item => item.Id == id);
                contextMenuOptions.Add(new MenuOption(id, definition.Label, !preferences.HiddenContextMenuItems.Contains(id)));
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        { ShowNotice("无法读取外观设置", error.Message, InfoBarSeverity.Warning); }
        BackgroundOpacitySlider.Value = savedBackgroundOpacity;
        ContextMenuOptionsList.ItemsSource = contextMenuOptions;
        UpdateBadgeCheckBox.IsChecked = updateBadgeEnabled;
        settingsReady = true;
        Closed += (_, _) =>
        {
            BackgroundOpacityPreviewChanged?.Invoke(savedBackgroundOpacity);
            UpdateBadgePreferenceChanged?.Invoke(savedUpdateBadgeEnabled);
        };
        UpdateSettingsState();
    }

    private void UpdateSettingsState()
    {
        if (!settingsReady) return;
        BackgroundOpacityValue.Text = BackgroundOpacitySlider.Value.ToString("0.#") + "%";
        SaveSettingsButton.IsEnabled = SettingsDirty;
        SettingsStatus.Text = SettingsDirty ? "正在预览 · 尚未保存" : "设置已保存";
        SaveButton.Visibility = selectedTabIndex == 2 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void BackgroundOpacityChanged(object sender, RangeBaseValueChangedEventArgs args)
    {
        if (!settingsReady) return;
        BackgroundOpacityPreviewChanged?.Invoke(args.NewValue);
        Changed();
    }

    private void EditorTabClicked(object sender, RoutedEventArgs args)
    {
        SelectEditorTab(ReferenceEquals(sender, WeeklyTabButton) ? 0
            : ReferenceEquals(sender, ExceptionTabButton) ? 1
            : ReferenceEquals(sender, SettingsTabButton) ? 2 : -1);
    }

    internal void SelectEditorTab(int index)
    {
        if (index is < 0 or > 2) return;
        selectedTabIndex = index;
        WeeklyTabPage.Visibility = index == 0 ? Visibility.Visible : Visibility.Collapsed;
        ExceptionTabPage.Visibility = index == 1 ? Visibility.Visible : Visibility.Collapsed;
        SettingsTabPage.Visibility = index == 2 ? Visibility.Visible : Visibility.Collapsed;
        WeeklyTabHighlight.Visibility = index == 0 ? Visibility.Visible : Visibility.Collapsed;
        ExceptionTabHighlight.Visibility = index == 1 ? Visibility.Visible : Visibility.Collapsed;
        SettingsTabHighlight.Visibility = index == 2 ? Visibility.Visible : Visibility.Collapsed;
        if (settingsReady) Changed();
    }

    private void ResetBackgroundOpacity(object sender, RoutedEventArgs args) =>
        BackgroundOpacitySlider.Value = DesktopSettings.DefaultBackgroundOpacity;

    private void MenuOptionChanged(object sender, RoutedEventArgs args)
    {
        if (sender is CheckBox { Tag: MenuOption option, IsChecked: bool enabled }) option.IsEnabled = enabled;
        Changed();
    }

    private void MenuListPointerPressed(object sender, PointerRoutedEventArgs args)
    {
        menuDragStartedOnHandle = false;
        var element = args.OriginalSource as DependencyObject;
        while (element is not null && !ReferenceEquals(element, ContextMenuOptionsList))
        {
            if (element is FrameworkElement { Name: "MenuDragHandle" })
            {
                menuDragStartedOnHandle = true;
                break;
            }
            element = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(element);
        }
    }

    private void MenuDragHandlePressed(object sender, PointerRoutedEventArgs args) => menuDragStartedOnHandle = true;

    private void MenuDragItemsStarting(object sender, DragItemsStartingEventArgs args)
    {
        if (menuDragStartedOnHandle)
        {
            menuDragStartedOnHandle = false;
            return;
        }
        args.Cancel = true;
    }

    private void MenuItemsReordered(ListViewBase sender, DragItemsCompletedEventArgs args) => Changed();

    private void UpdateBadgeSettingChanged(object sender, RoutedEventArgs args)
    {
        updateBadgeEnabled = UpdateBadgeCheckBox.IsChecked == true;
        UpdateBadgePreferenceChanged?.Invoke(updateBadgeEnabled);
        Changed();
    }

    private bool SaveAppearance()
    {
        try
        {
            var order = contextMenuOptions.Select(item => item.Id).ToArray();
            var hidden = contextMenuOptions.Where(item => !item.IsEnabled).Select(item => item.Id).ToArray();
            settings.SavePreferences(new DesktopPreferences(BackgroundOpacitySlider.Value, order, hidden, updateBadgeEnabled));
            savedBackgroundOpacity = BackgroundOpacitySlider.Value;
            savedContextMenuOrder = order;
            savedHiddenMenuItems = hidden.Order(StringComparer.Ordinal).ToArray();
            savedUpdateBadgeEnabled = updateBadgeEnabled;
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
