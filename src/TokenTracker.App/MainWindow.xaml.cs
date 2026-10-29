using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TokenTracker.Core.Models;
using TokenTracker_App.Pages;

namespace TokenTracker_App;

public sealed partial class MainWindow : Window
{
    private readonly Dictionary<string, NavigationViewItem> _modelItems =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ModelDescriptor> _modelInfo =
        new(StringComparer.OrdinalIgnoreCase);
    private NavigationViewItem? _homeItem;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _menuTimer;
    private string _menuSignature = string.Empty;
    private bool _menuBuilt;
    private bool _showingHome = true;
    private string? _currentModelId;
    private bool _suppressNavigation;
    private Action? _backAction;

    public static MainWindow? CurrentWindow { get; private set; }

    public MainWindow()
    {
        InitializeComponent();
        CurrentWindow = this;

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        AppWindow.SetIcon("Assets/AppIcon.ico");
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1440, 900));

        RefreshModelMenu();
        ShowHome();

        _menuTimer = DispatcherQueue.CreateTimer();
        _menuTimer.Interval = TimeSpan.FromSeconds(5);
        _menuTimer.Tick += (_, _) => RefreshModelMenu();
        _menuTimer.Start();
    }

    public void ShowHome()
    {
        _showingHome = true;
        _currentModelId = null;
        _backAction = null;
        UpdateBackButton();
        PageHost.Content = new MainPage();
        if (_homeItem is not null)
        {
            SelectNavItem(_homeItem);
        }
    }

    public void ShowModel(string modelId, string displayName)
    {
        _showingHome = false;
        _currentModelId = modelId;
        _backAction = null;
        UpdateBackButton();
        PageHost.Content = new ModelPage(modelId, displayName);
        if (_modelItems.TryGetValue(modelId, out var item))
        {
            SelectNavItem(item);
        }
    }

    public void ShowSessionDetail(
        string sessionId,
        string? returnModelId = null,
        string? returnModelName = null)
    {
        if (returnModelId is not null)
        {
            _showingHome = false;
            _currentModelId = returnModelId;
            _backAction = () => ShowModel(returnModelId, returnModelName ?? returnModelId);
        }
        else
        {
            _backAction = ShowHome;
        }

        UpdateBackButton();
        PageHost.Content = new SessionDetailPage(sessionId);

        if (returnModelId is not null &&
            _modelItems.TryGetValue(returnModelId, out var item))
        {
            SelectNavItem(item);
        }
    }

    private void TitleBar_BackRequested(TitleBar sender, object args) =>
        _backAction?.Invoke();

    private void TitleBar_PaneToggleRequested(TitleBar sender, object args) =>
        Nav.IsPaneOpen = !Nav.IsPaneOpen;

    private void UpdateBackButton()
    {
        var canGoBack = _backAction is not null;
        AppTitleBar.IsBackButtonVisible = canGoBack;
        AppTitleBar.IsBackButtonEnabled = canGoBack;
    }

    private void Nav_ItemInvoked(
        NavigationView sender,
        NavigationViewItemInvokedEventArgs args)
    {
        NavigateForTag((args.InvokedItemContainer as NavigationViewItem)?.Tag as string);
    }

    private void Nav_SelectionChanged(
        NavigationView sender,
        NavigationViewSelectionChangedEventArgs args)
    {
        if (_suppressNavigation)
        {
            return;
        }

        NavigateForTag((args.SelectedItemContainer as NavigationViewItem)?.Tag as string);
    }

    private void NavigateForTag(string? tag)
    {
        if (tag == "home")
        {
            if (PageHost.Content is not MainPage)
            {
                ShowHome();
            }

            return;
        }

        if (tag is null || !tag.StartsWith("model:", StringComparison.Ordinal))
        {
            return;
        }

        var modelId = tag["model:".Length..];
        if (PageHost.Content is ModelPage current &&
            string.Equals(current.ViewModel.ModelId, modelId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var descriptor = _modelInfo.TryGetValue(modelId, out var known)
            ? known
            : ModelNaming.Describe(modelId);
        ShowModel(modelId, descriptor.DisplayName);
    }

    private void SelectNavItem(NavigationViewItem item)
    {
        _suppressNavigation = true;
        Nav.SelectedItem = item;
        _suppressNavigation = false;
    }

    private void RefreshModelMenu()
    {
        IReadOnlyList<ModelRow> models;
        try
        {
            models = App.Store?.GetModels(0) ?? Array.Empty<ModelRow>();
        }
        catch
        {
            models = Array.Empty<ModelRow>();
        }

        var descriptors = models
            .Select(row => ModelNaming.Describe(row.Model))
            .GroupBy(model => model.ModelId)
            .Select(group => group.First())
            .ToList();

        var signature = string.Join(
            "|",
            descriptors.Select(model => model.ModelId).OrderBy(id => id, StringComparer.OrdinalIgnoreCase));
        if (_menuBuilt && signature == _menuSignature)
        {
            return;
        }

        _menuSignature = signature;
        _menuBuilt = true;

        Nav.MenuItems.Clear();
        _modelItems.Clear();
        _modelInfo.Clear();

        _homeItem = new NavigationViewItem
        {
            Content = "主页",
            Tag = "home",
            Icon = new SymbolIcon(Symbol.Home),
        };
        Nav.MenuItems.Add(_homeItem);

        foreach (var providerGroup in descriptors
                     .GroupBy(model => model.Provider)
                     .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase))
        {
            var providerItem = new NavigationViewItem
            {
                Content = providerGroup.Key,
                IsExpanded = true,
                SelectsOnInvoked = false,
                Icon = new SymbolIcon(Symbol.Folder),
            };

            foreach (var model in providerGroup.OrderBy(
                         item => item.ShortName,
                         StringComparer.OrdinalIgnoreCase))
            {
                var child = new NavigationViewItem
                {
                    Content = model.ShortName,
                    Tag = "model:" + model.ModelId,
                };
                ToolTipService.SetToolTip(child, model.DisplayName);
                providerItem.MenuItems.Add(child);
                _modelItems[model.ModelId] = child;
                _modelInfo[model.ModelId] = model;
            }

            Nav.MenuItems.Add(providerItem);
        }

        if (descriptors.Count == 0)
        {
            Nav.MenuItems.Add(new NavigationViewItem
            {
                Content = "暂无模型",
                IsEnabled = false,
            });
        }

        if (!_showingHome &&
            _currentModelId is not null &&
            _modelItems.TryGetValue(_currentModelId, out var selected))
        {
            SelectNavItem(selected);
        }
        else if (_showingHome && _homeItem is not null)
        {
            SelectNavItem(_homeItem);
        }
        else if (!_showingHome)
        {
            ShowHome();
        }
    }
}
