using StardewModdingAPI;
using StardewModdingAPI.Events;

namespace StardewNpcAgent.Integration;

/// <summary>
/// 正式 runtime 使用的 SMAPI 公共事件绑定；不包含业务判断或网络逻辑。
/// </summary>
public sealed class SmapiEventBindings
{
    private readonly IModHelper helper;
    private readonly SaveSessionRuntime runtime;
    private bool initialized;

    public SmapiEventBindings(IModHelper helper, SaveSessionRuntime runtime)
    {
        this.helper = helper ?? throw new ArgumentNullException(nameof(helper));
        this.runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
    }

    public void Initialize()
    {
        if (initialized)
        {
            throw new InvalidOperationException("SMAPI event bindings 不能重复初始化。");
        }

        helper.Events.GameLoop.SaveLoaded += OnSaveLoaded;
        helper.Events.GameLoop.DayStarted += OnDayStarted;
        helper.Events.GameLoop.DayEnding += OnDayEnding;
        helper.Events.GameLoop.Saved += OnSaved;
        helper.Events.GameLoop.OneSecondUpdateTicked += OnOneSecondUpdateTicked;
        helper.Events.GameLoop.UpdateTicked += OnUpdateTicked;
        helper.Events.GameLoop.ReturnedToTitle += OnReturnedToTitle;
        helper.Events.Player.LevelChanged += OnLevelChanged;
        helper.Events.Player.Warped += OnWarped;
        helper.Events.Player.InventoryChanged += OnInventoryChanged;
        helper.Events.Content.LocaleChanged += OnLocaleChanged;
        helper.Events.Content.AssetsInvalidated += OnAssetsInvalidated;
        helper.Events.Content.AssetRequested += OnAssetRequested;
        helper.Events.Display.MenuChanged += OnMenuChanged;
        helper.Events.Display.RenderedActiveMenu += OnRenderedActiveMenu;
        initialized = true;
    }

    private void OnSaveLoaded(object? sender, SaveLoadedEventArgs eventArgs)
    {
        runtime.HandleSaveLoaded();
    }

    private void OnDayStarted(object? sender, DayStartedEventArgs eventArgs)
    {
        runtime.HandleDayStarted();
    }

    private void OnDayEnding(object? sender, DayEndingEventArgs eventArgs)
    {
        runtime.HandleDayEnding();
    }

    private void OnSaved(object? sender, SavedEventArgs eventArgs)
    {
        runtime.HandleSaved();
    }

    private void OnOneSecondUpdateTicked(
        object? sender,
        OneSecondUpdateTickedEventArgs eventArgs)
    {
        runtime.HandleOneSecondUpdateTicked();
    }

    private void OnUpdateTicked(object? sender, UpdateTickedEventArgs eventArgs)
    {
        runtime.HandleUpdateTicked();
    }

    private void OnReturnedToTitle(object? sender, ReturnedToTitleEventArgs eventArgs)
    {
        runtime.HandleReturnedToTitle();
    }

    private void OnLevelChanged(object? sender, LevelChangedEventArgs eventArgs)
    {
        runtime.HandleLevelChanged(eventArgs);
    }

    private void OnWarped(object? sender, WarpedEventArgs eventArgs)
    {
        runtime.HandleWarped(eventArgs);
    }

    private void OnInventoryChanged(object? sender, InventoryChangedEventArgs eventArgs)
    {
        runtime.HandleInventoryChanged(eventArgs);
    }

    private void OnLocaleChanged(object? sender, LocaleChangedEventArgs eventArgs)
    {
        runtime.HandleLocaleChanged();
    }

    private void OnAssetsInvalidated(object? sender, AssetsInvalidatedEventArgs eventArgs)
    {
        runtime.HandleAssetsInvalidated(eventArgs);
    }

    private void OnAssetRequested(object? sender, AssetRequestedEventArgs eventArgs)
    {
        runtime.HandleAssetRequested(eventArgs);
    }

    private void OnMenuChanged(object? sender, MenuChangedEventArgs eventArgs)
    {
        runtime.HandleMenuChanged(eventArgs);
    }

    private void OnRenderedActiveMenu(object? sender, RenderedActiveMenuEventArgs eventArgs)
    {
        runtime.HandleRenderedActiveMenu();
    }
}
