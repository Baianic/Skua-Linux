using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Skua.Backend.Linux.Bridge;
using Skua.Core.Interfaces;
using Skua.Core.Models;
using Skua.Core.Models.Items;
using Skua.Core.Models.Monsters;
using Skua.Core.Models.Quests;
using Skua.Core.Models.Shops;
using Skua.Core.Models.Skills;
using Skua.Core.Messaging;
using Skua.Core.ViewModels;
using System.Collections;
using System.Collections.Specialized;
using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace Skua.Backend.Linux.Services;

/// <summary>
/// Linux UI command adapter for functionality that already belongs to Skua.Core.
/// This class intentionally contains no AQW automation algorithms: it only exposes
/// the original Core interfaces/services to the Electron UI.
/// </summary>
public sealed class SkuaParityCommandService : IAsyncDisposable
{
    private readonly IServiceProvider _services;
    private readonly IScriptManager _scriptManager;
    private readonly IScriptOption _options;
    private readonly IScriptMap _scriptMap;
    private readonly IMapService _mapService;
    private readonly IScriptServers _servers;
    private readonly IScriptPlayer _player;
    private readonly IScriptDrop _drops;
    private readonly IScriptQuest _quests;
    private readonly IScriptBoost _boosts;
    private readonly IScriptBotStats _stats;
    private readonly IScriptAuto _auto;
    private readonly IScriptInventory _inventory;
    private readonly IScriptSend _send;
    private readonly IScriptBank _bank;
    private readonly IAdvancedSkillContainer _advancedSkills;
    private readonly ISettingsService _settings;
    private readonly ILogService _logs;
    private readonly IPluginManager _plugins;
    private readonly IFlashUtil _flash;
    private readonly IScriptShop _shops;
    private readonly IQuestDataLoaderService _questLoader;
    private readonly IGrabberService _grabber;
    private readonly IScriptHouseInv _houseInventory;
    private readonly IScriptMap _map;
    private readonly IScriptKill _kill;

    private readonly object _notifyLock = new();
    private readonly List<string> _notifyDrops = new();
    private readonly ConcurrentQueue<NotifyDropEvent> _notifyEvents = new();
    private int _notifySoundCount = 5;
    private int _notifySoundDelay = 200;

    private static readonly string[] DefaultBackgrounds =
    {
        "Black", "Generic2.swf", "Skyguard.swf", "Kezeroth.swf",
        "Mirror.swf", "DageScorn.swf", "ravenloss2.swf"
    };

    private readonly object _loaderLock = new();
    private CancellationTokenSource? _loaderCts;
    private Task? _loaderTask;
    private bool _loaderIsLoading;
    private string _loaderProgress = string.Empty;
    private List<QuestData> _loaderQuestData = new();

    private readonly object _packetLoggerLock = new();
    private readonly List<string> _packetLogs = new();
    private readonly Dictionary<string, bool> _packetLoggerFilters = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Combat"] = true,
        ["User Data"] = true,
        ["Join"] = true,
        ["Jump"] = true,
        ["Movement"] = true,
        ["Get Map"] = true,
        ["Quest"] = true,
        ["Shop"] = true,
        ["Equip"] = true,
        ["Drop"] = true,
        ["Chat"] = true,
        ["Misc"] = true
    };
    private bool _packetLoggerEnabled;

    private readonly object _spammerLock = new();
    private readonly List<string> _spammerPackets = new();
    private CancellationTokenSource? _spammerCts;
    private Task? _spammerTask;
    private bool _spammerSendToClient;
    private int _spammerDelay = 1000;
    private int _spammerSelectedIndex = -1;

    public SkuaParityCommandService(IServiceProvider services)
    {
        _services = services;
        _scriptManager = services.GetRequiredService<IScriptManager>();
        _options = services.GetRequiredService<IScriptOption>();
        _scriptMap = services.GetRequiredService<IScriptMap>();
        _mapService = services.GetRequiredService<IMapService>();
        _servers = services.GetRequiredService<IScriptServers>();
        _player = services.GetRequiredService<IScriptPlayer>();
        _drops = services.GetRequiredService<IScriptDrop>();
        _quests = services.GetRequiredService<IScriptQuest>();
        _boosts = services.GetRequiredService<IScriptBoost>();
        _stats = services.GetRequiredService<IScriptBotStats>();
        _auto = services.GetRequiredService<IScriptAuto>();
        _inventory = services.GetRequiredService<IScriptInventory>();
        _send = services.GetRequiredService<IScriptSend>();
        _bank = services.GetRequiredService<IScriptBank>();
        _advancedSkills = services.GetRequiredService<IAdvancedSkillContainer>();
        _settings = services.GetRequiredService<ISettingsService>();
        _logs = services.GetRequiredService<ILogService>();
        _plugins = services.GetRequiredService<IPluginManager>();
        _flash = services.GetRequiredService<IFlashUtil>();
        _shops = services.GetRequiredService<IScriptShop>();
        _questLoader = services.GetRequiredService<IQuestDataLoaderService>();
        _grabber = services.GetRequiredService<IGrabberService>();
        _houseInventory = services.GetRequiredService<IScriptHouseInv>();
        _map = services.GetRequiredService<IScriptMap>();
        _kill = services.GetRequiredService<IScriptKill>();

        _flash.FlashCall += OnFlashCall;
        StrongReferenceMessenger.Default.Register<SkuaParityCommandService, ItemDroppedMessage, int>(
            this,
            (int)MessageChannels.GameEvents,
            static (recipient, message) => recipient.OnItemDropped(message));

        try
        {
            _plugins.Initialize();
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[Plugins] Initialize failed: {exception}");
        }
    }

    public async Task<BridgeCommandResult?> HandleAsync(
        BridgeCommand command,
        CancellationToken cancellationToken)
    {
        try
        {
            return command.Name switch
            {
                "parity.capabilities" => Ok(GetCapabilities()),

                "script.options.status" => await ScriptOptionsStatusAsync(cancellationToken),
                "script.options.set" => ScriptOptionsSet(command),
                "script.options.defaults" => ScriptOptionsDefaults(),

                "options.game.status" => GameOptionsStatus(),
                "options.game.set" => GameOptionsSet(command),
                "options.game.save" => Run(() => _options.Save()),
                "options.game.reset" => Run(() => _options.Reset()),
                "options.game.default" => Run(() => _options.ResetToDefault()),
                "options.game.reloadMap" => Run(() => _scriptMap.Reload()),
                "options.game.upgrade" => GameNameColor(command, 0x8CD5FF),
                "options.game.staff" => GameNameColor(command, 0xFECB38),

                "options.application.status" => ApplicationOptionsStatus(),
                "options.application.set" => ApplicationOptionsSet(command),
                "junk.warning" => JunkWarning(command),

                "themes.status" => ThemesStatus(),
                "themes.save" => ThemesSave(command),
                "themes.setCurrent" => ThemesSetCurrent(command),
                "themes.remove" => ThemesRemove(command),
                "themes.background.set" => await ThemesBackgroundSetAsync(command),
                "themes.background.import" => await ThemesBackgroundImportAsync(command),

                "notify.status" => NotifyStatus(),
                "notify.add" => NotifyAdd(command),
                "notify.remove" => NotifyRemove(command),
                "notify.clear" => NotifyClear(),
                "notify.configure" => NotifyConfigure(command),
                "notify.test" => NotifyTest(),
                "notify.poll" => NotifyPoll(),

                "runtime.drops.status" => RuntimeDropsStatus(),
                "runtime.drops.add" => RuntimeDropsAdd(command),
                "runtime.drops.remove" => RuntimeDropsRemove(command),
                "runtime.drops.clear" => Run(_drops.Clear),
                "runtime.drops.toggle" => await RuntimeDropsToggleAsync(),
                "runtime.drops.configure" => RuntimeDropsConfigure(command),

                "runtime.quests.status" => RuntimeQuestsStatus(),
                "runtime.quests.add" => RuntimeQuestsAdd(command),
                "runtime.quests.remove" => RuntimeQuestsRemove(command),
                "runtime.quests.clear" => Run(_quests.UnregisterAllQuests),

                "runtime.boosts.status" => RuntimeBoostsStatus(),
                "runtime.boosts.set" => RuntimeBoostsSet(command),
                "runtime.boosts.toggle" => await RuntimeBoostsToggleAsync(),
                "runtime.boosts.detect" => RuntimeBoostsDetect(command),

                "travel.list" => TravelList(),
                "travel.current" => TravelCurrent(),
                "travel.add" => TravelAdd(command),
                "travel.update" => TravelUpdate(command),
                "travel.remove" => TravelRemove(command),
                "travel.clear" => TravelClear(),
                "travel.go" => TravelGo(command),
                "travel.settings" => TravelSettings(command),

                "drops.current" => CurrentDropsStatus(),
                "drops.pick" => CurrentDropsPick(command),
                "drops.pickAll" => Run(() => _drops.PickupAll(true)),
                "drops.pickAC" => Run(_drops.PickupACItems),

                "loader.status" => LoaderStatus(),
                "loader.load" => LoaderLoad(command),
                "loader.quests.get" => await LoaderGetQuestsAsync(),
                "loader.quests.update" => LoaderUpdate(command),
                "loader.quests.range" => LoaderUpdateRange(command),
                "loader.quests.cancel" => LoaderCancel(),
                "loader.quests.fakeComplete" => LoaderFakeComplete(command),

                "grabber.status" => GrabberStatus(command),
                "grabber.action" => await GrabberActionAsync(command, cancellationToken),

                "stats.status" => StatsStatus(),
                "stats.reset" => Run(_stats.Reset),
                "stats.getSpace" => Run(_stats.GetSpace),

                "console.run" => ConsoleRun(command),

                "logs.status" => LogsStatus(command),
                "logs.clear" => LogsClear(command),

                "auto.status" => AutoStatus(),
                "auto.equip" => AutoEquip(command),
                "auto.startAttack" => AutoStart(command, hunt: false),
                "auto.startHunt" => AutoStart(command, hunt: true),
                "auto.stop" => await AutoStopAsync(),

                "skills.status" => SkillsStatus(),
                "skills.save" => SkillsSave(command),
                "skills.remove" => SkillsRemove(command),
                "skills.reset" => Run(() => _advancedSkills.ResetSkillsSets()),
                "skills.sync" => Run(() => _advancedSkills.SyncSkills()),
                "skills.reload" => Run(() => _advancedSkills.LoadSkills()),
                "skills.parse" => SkillsParse(command),
                "skills.build" => SkillsBuild(command),

                "packets.spammer.status" => PacketSpammerStatus(),
                "packets.spammer.configure" => PacketSpammerConfigure(command),
                "packets.spammer.add" => PacketSpammerAdd(command),
                "packets.spammer.remove" => PacketSpammerRemove(command),
                "packets.spammer.clear" => PacketSpammerClear(),
                "packets.spammer.send" => PacketSpammerSend(command),
                "packets.spammer.start" => PacketSpammerStart(),
                "packets.spammer.stop" => await PacketSpammerStopAsync(),

                "packets.logger.status" => PacketLoggerStatus(),
                "packets.logger.enable" => PacketLoggerEnable(command),
                "packets.logger.filters" => PacketLoggerFilters(command),
                "packets.logger.clear" => PacketLoggerClear(),

                "packets.interceptor.servers" => await PacketInterceptorServersAsync(),
                "packets.interceptor.relogin" => await PacketInterceptorReloginAsync(command, cancellationToken),

                "plugins.status" => PluginsStatus(),
                "plugins.load" => PluginsLoad(command),
                "plugins.unload" => PluginsUnload(command),
                "plugins.unloadAll" => PluginsUnloadAll(),
                "plugins.options.status" => PluginOptionsStatus(command),
                "plugins.options.set" => PluginOptionsSet(command),

                "hotkeys.status" => HotKeysStatus(),
                "hotkeys.save" => HotKeysSave(command),

                "corebots.status" => CoreBotsStatus(),
                "corebots.save" => CoreBotsSave(command),

                "junk.status" => JunkStatus(),
                "junk.set" => JunkSet(command),
                "junk.clear" => JunkClear(),
                "junk.sellAll" => JunkSellAll(),

                _ => null
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return Fail(exception.ToString());
        }
    }

    private object GetCapabilities() => new
    {
        phase = "functional-parity-v3",
        functional = new[]
        {
            "Script Options",
            "Game Options",
            "Application Options settings",
            "Runtime / To Pickup Drops",
            "Runtime / Registered Quests",
            "Runtime / Boosts",
            "Fast Travel",
            "Current Drops",
            "Loader",
            "Grabber",
            "Stats",
            "Console",
            "Logs",
            "Auto Attack / Auto Hunt",
            "Advanced Skills sets and rule DSL",
            "Packet Spammer",
            "Packet Logger",
            "Packet Interceptor server selection / inline Linux capture",
            "Plugins load/unload/options",
            "HotKeys persistence and bindings",
            "CoreBots Options/Other/Loadout storage",
            "Junk Items",
            "Application Themes settings/backgrounds",
            "Notify Drop event queue/audio handoff",
            "Script repository refresh/download/update/cancel"
        },
        platformAdapters = new[]
        {
            "Application Themes are rendered by Electron/CSS while preserving original serialized settings",
            "Notify Drop audio is played by Electron/WebAudio",
            "Packet Interceptor is implemented in the existing Linux WebSocket/TCP proxy",
            "HotKeys are executed in the Electron renderer"
        }
    };

    // ---------------------------------------------------------------------
    // Script Options
    // ---------------------------------------------------------------------

    private async Task<BridgeCommandResult> ScriptOptionsStatusAsync(CancellationToken cancellationToken)
    {
        if (_scriptManager.ScriptRunning)
            return Fail("script-running");
        if (string.IsNullOrWhiteSpace(_scriptManager.LoadedScript) || !File.Exists(_scriptManager.LoadedScript))
            return Fail("no-script-loaded");

        object? compiled = await Task.Run(
            () => _scriptManager.Compile(File.ReadAllText(_scriptManager.LoadedScript)),
            cancellationToken);
        _scriptManager.LoadScriptConfig(compiled);

        IOptionContainer? config = _scriptManager.Config;
        if (config is null || (config.Options.Count == 0 && config.MultipleOptions.Count == 0))
            return Ok(new { available = false, options = Array.Empty<object>() });

        List<object> items = new();
        foreach (IOption option in config.Options)
            items.Add(DescribeOption(config, "Options", option));
        foreach (KeyValuePair<string, List<IOption>> group in config.MultipleOptions)
            foreach (IOption option in group.Value)
                items.Add(DescribeOption(config, group.Key, option));

        return Ok(new
        {
            available = true,
            optionsFile = config.OptionsFile,
            options = items
        });
    }

    private static object DescribeOption(IOptionContainer container, string category, IOption option)
    {
        string[] enumValues = option.Type.IsEnum
            ? Enum.GetNames(option.Type).Select(x => x.Replace('_', ' ')).ToArray()
            : Array.Empty<string>();
        return new
        {
            category,
            option.Name,
            option.DisplayName,
            option.Description,
            defaultValue = option.DefaultValue?.ToString() ?? string.Empty,
            value = container.GetDirect(option),
            transient = option.Transient,
            type = option.Type.FullName ?? option.Type.Name,
            isEnum = option.Type.IsEnum,
            enumValues
        };
    }

    private BridgeCommandResult ScriptOptionsSet(BridgeCommand command)
    {
        IOptionContainer? config = _scriptManager.Config;
        if (config is null)
            return Fail("script-options-not-loaded");
        string category = ArgString(command, 0) ?? "Options";
        string? name = ArgString(command, 1);
        string value = ArgString(command, 2) ?? string.Empty;
        if (string.IsNullOrWhiteSpace(name))
            return Fail("option-name-required");
        if (string.Equals(category, "Options", StringComparison.Ordinal))
            config.Set(name, value);
        else
            config.Set(category, name, value);
        config.Save();
        return Ok(new { saved = true, category, name, value });
    }

    private BridgeCommandResult ScriptOptionsDefaults()
    {
        IOptionContainer? config = _scriptManager.Config;
        if (config is null)
            return Fail("script-options-not-loaded");
        config.SetDefaults();
        config.Save();
        return Ok(new { reset = true });
    }

    // ---------------------------------------------------------------------
    // Game and Application options
    // ---------------------------------------------------------------------

    private BridgeCommandResult GameOptionsStatus()
    {
        HashSet<string> excluded = new(StringComparer.Ordinal)
        {
            nameof(IScriptOption.OptionDictionary),
            nameof(IScriptOption.GuildColor),
            nameof(IScriptOption.NameColor),
            "IsActive"
        };
        List<object> items = new();
        foreach (PropertyInfo property in _options.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            if (excluded.Contains(property.Name) || !property.CanRead || !property.CanWrite)
                continue;
            Type type = property.PropertyType;
            if (type != typeof(bool) && type != typeof(string) && type != typeof(int))
                continue; // exactly mirrors Options.CreateGameOptions
            items.Add(new
            {
                name = property.Name,
                displayName = Decamelize(property.Name),
                type = type.Name,
                value = property.GetValue(_options),
                suffix = property.Name == nameof(IScriptOption.SetFPS) ? "FPS" : null
            });
        }

        IScriptServers servers = _services.GetRequiredService<IScriptServers>();
        return Ok(new
        {
            options = items,
            servers = servers.CachedServers.Select(server => server.Name).ToArray(),
            selectedServer = _options.ReloginServer
        });
    }

    private BridgeCommandResult GameOptionsSet(BridgeCommand command)
    {
        string? name = ArgString(command, 0);
        if (string.IsNullOrWhiteSpace(name))
            return Fail("option-name-required");
        PropertyInfo? property = _options.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
        if (property is null || !property.CanWrite)
            return Fail("option-not-found");
        object? value = ConvertJsonArg(command, 1, property.PropertyType);
        property.SetValue(_options, value);
        return Ok(new { name, value = property.GetValue(_options) });
    }

    private BridgeCommandResult GameNameColor(BridgeCommand command, int enabledColor)
    {
        bool enabled = ArgBool(command, 0, false);
        _flash.SetGameObject("world.myAvatar.pMC.pname.ti.textColor", enabled ? enabledColor : 0xFFFFFF);
        return Ok(new { enabled });
    }

    private static readonly (string Name, string Description, string Key, Type Type, object Default)[] AppOptionDefinitions =
    {
        ("Auto Update Scripts", "Whether to auto update scripts when launching the Manager, needs Check for Scripts Updates to be true", "AutoUpdateBotScripts", typeof(bool), true),
        ("Check for Script Updates", "Whether to check for scripts updates when launching the Manager", "CheckBotScriptsUpdates", typeof(bool), true),
        ("Auto Update AdvanceSkill Sets", "Whether to auto update advance skill sets when launching the Manager, needs Check for AdvanceSkill Sets updates to be true", "AutoUpdateAdvanceSkillSetsUpdates", typeof(bool), true),
        ("Check for AdvanceSkill Sets Updates", "Whether to check for scripts updates when launching the Manager", "CheckAdvanceSkillSetsUpdates", typeof(bool), true),
        ("Auto Update Junk Items", "Whether to auto update junk items when launching the Manager, needs Check for Junk Items Updates to be true", "AutoUpdateJunkItems", typeof(bool), true),
        ("Check for Junk Items Updates", "Whether to check for junk items updates when launching the Manager", "CheckJunkItemsUpdates", typeof(bool), true),
        ("Show Username in Title", "Whether to show the current username in the window title and tray tooltip", "ShowUsernameInTitle", typeof(bool), false),
        ("* Client Animation Frame-rate", "Client side animation frame-rate setting", "AnimationFrameRate", typeof(int), 30)
    };

    private BridgeCommandResult ApplicationOptionsStatus()
    {
        List<object> items = new();
        foreach (var definition in AppOptionDefinitions)
        {
            object? value = definition.Type == typeof(bool)
                ? _settings.Get(definition.Key, (bool)definition.Default)
                : _settings.Get(definition.Key, (int)definition.Default);
            items.Add(new
            {
                name = definition.Name,
                description = definition.Description,
                key = definition.Key,
                type = definition.Type.Name,
                value,
                suffix = definition.Key == "AnimationFrameRate" ? "FPS" : null
            });
        }
        return Ok(new { options = items, clearCacheHandledBy = "electron-session", username = _player.Username });
    }

    private BridgeCommandResult ApplicationOptionsSet(BridgeCommand command)
    {
        string? key = ArgString(command, 0);
        var definition = AppOptionDefinitions.FirstOrDefault(x => x.Key == key);
        if (definition.Key is null)
            return Fail("application-option-not-found");
        if (definition.Type == typeof(bool))
            _settings.Set(key!, ArgBool(command, 1, (bool)definition.Default));
        else
        {
            int value = Math.Max(1, ArgInt(command, 1, (int)definition.Default));
            _settings.Set(key!, value);
        }
        return Ok(new { key, saved = true });
    }

    // ---------------------------------------------------------------------
    // Application Themes (Linux/Electron adapter)
    // ---------------------------------------------------------------------

    private BridgeCommandResult ThemesStatus(
        string? backgroundApplyMode = null,
        bool backgroundReloadSuggested = false,
        bool backgroundAppliedImmediately = false)
    {
        StringCollection defaults =
            _settings.Get<StringCollection>("DefaultThemes") ?? new();
        StringCollection users =
            _settings.Get<StringCollection>("UserThemes") ?? new();
        string current = _settings.Get(
            "CurrentTheme",
            "Skua,Dark,#FF607D8B,#FF607D8B,#FF000000,#FF000000,true,4.5,Medium,All");

        Directory.CreateDirectory(ClientFileSources.SkuaThemesDIR);
        List<string> backgrounds = new(DefaultBackgrounds);
        backgrounds.AddRange(
            Directory.GetFiles(ClientFileSources.SkuaThemesDIR, "*.swf")
                .Select(Path.GetFileName)
                .Where(name => !string.IsNullOrWhiteSpace(name))!
                .Cast<string>());

        string sBG = _settings.Get("sBG", "Generic2.swf");
        string? customBackgroundPath =
            NormalizeCustomBackgroundPathForLinux(
                _settings.Get<string?>("CustomBackgroundPath", null));
        string currentBackground = string.IsNullOrWhiteSpace(customBackgroundPath)
            ? sBG
            : GetBackgroundFileName(customBackgroundPath!);

        return Ok(new
        {
            presets = defaults.Cast<string>().Select(ParseTheme).ToArray(),
            userThemes = users.Cast<string>().Select(ParseTheme).ToArray(),
            current = ParseTheme(current),
            currentSerialized = current,
            backgrounds = backgrounds.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            currentBackground,
            themesDirectory = ClientFileSources.SkuaThemesDIR,
            backgroundRepository = "https://github.com/auqw/SkuaBackgrounds",
            backgroundApplyMode,
            backgroundReloadSuggested,
            backgroundAppliedImmediately
        });
    }

    private BridgeCommandResult ThemesSave(BridgeCommand command)
    {
        string name = (ArgString(command, 0) ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(name)) return Fail("theme-name-required");

        string baseTheme = NormalizeBaseTheme(ArgString(command, 1));
        string primary = NormalizeThemeColor(ArgString(command, 2), "#FF607D8B");
        string secondary = NormalizeThemeColor(ArgString(command, 3), primary);
        string primaryForeground = NormalizeThemeColor(ArgString(command, 4), "#FF000000");
        string secondaryForeground = NormalizeThemeColor(ArgString(command, 5), primaryForeground);
        bool useAdjustment = ArgBool(command, 6, false);
        double ratio = ArgDouble(command, 7, 4.5);
        string contrast = ArgString(command, 8) ?? "Medium";
        string colorSelection = ArgString(command, 9) ?? "All";

        string serialized = SerializeTheme(
            name,
            baseTheme,
            primary,
            secondary,
            primaryForeground,
            secondaryForeground,
            useAdjustment,
            ratio,
            contrast,
            colorSelection);

        StringCollection users =
            _settings.Get<StringCollection>("UserThemes") ?? new();
        List<string> updated = users.Cast<string>().ToList();
        int existingIndex = updated.FindIndex(value =>
            string.Equals(ParseThemeName(value), name, StringComparison.Ordinal));
        if (existingIndex >= 0) updated[existingIndex] = serialized;
        else updated.Add(serialized);

        StringCollection saved = new();
        saved.AddRange(updated.ToArray());
        _settings.Set("UserThemes", saved);
        _settings.Set("CurrentTheme", serialized);
        return ThemesStatus();
    }

    private BridgeCommandResult ThemesSetCurrent(BridgeCommand command)
    {
        string? requested = ArgString(command, 0);
        if (string.IsNullOrWhiteSpace(requested)) return Fail("theme-required");

        IEnumerable<string> candidates =
            (_settings.Get<StringCollection>("DefaultThemes") ?? new()).Cast<string>()
            .Concat((_settings.Get<StringCollection>("UserThemes") ?? new()).Cast<string>());
        string? serialized = requested.Contains(',')
            ? requested
            : candidates.FirstOrDefault(value =>
                string.Equals(ParseThemeName(value), requested, StringComparison.Ordinal));
        if (string.IsNullOrWhiteSpace(serialized)) return Fail("theme-not-found");
        _settings.Set("CurrentTheme", serialized);
        return ThemesStatus();
    }

    private BridgeCommandResult ThemesRemove(BridgeCommand command)
    {
        string? name = ArgString(command, 0);
        if (string.IsNullOrWhiteSpace(name)) return Fail("theme-name-required");
        StringCollection users = _settings.Get<StringCollection>("UserThemes") ?? new();
        List<string> remaining = users.Cast<string>()
            .Where(value => !string.Equals(ParseThemeName(value), name, StringComparison.Ordinal))
            .ToList();
        StringCollection saved = new();
        saved.AddRange(remaining.ToArray());
        _settings.Set("UserThemes", saved);

        string current = _settings.Get("CurrentTheme", string.Empty);
        if (string.Equals(ParseThemeName(current), name, StringComparison.Ordinal))
        {
            StringCollection defaults = _settings.Get<StringCollection>("DefaultThemes") ?? new();
            if (defaults.Count > 0) _settings.Set("CurrentTheme", defaults[0]!);
        }
        return ThemesStatus();
    }

    private async Task<BridgeCommandResult> ThemesBackgroundSetAsync(BridgeCommand command)
    {
        string? background = ArgString(command, 0);
        if (string.IsNullOrWhiteSpace(background)) return Fail("background-required");

        Directory.CreateDirectory(ClientFileSources.SkuaThemesDIR);
        string localPath = Path.Combine(ClientFileSources.SkuaThemesDIR, background);
        bool localCustom = File.Exists(localPath) &&
            !DefaultBackgrounds.Contains(background, StringComparer.OrdinalIgnoreCase);

        string sBG;
        string? customPath;
        if (localCustom)
        {
            sBG = "hideme.swf";
            customPath = BuildRuntimeBackgroundUrl(background, localPath);
        }
        else
        {
            sBG = background;
            customPath = null;
        }

        _settings.Set("sBG", sBG);
        _settings.Set<string?>("CustomBackgroundPath", customPath);

        // Custom SWFs can be applied immediately because Electron exposes the
        // user's themes folder over the same localhost origin used by Ruffle.
        // Default AQW backgrounds are consumed while the game client is being
        // created, so changing only game.params.sBG after load is not enough to
        // repaint the current login background. Preserve the running session:
        // suggest a renderer/game reload only when the player is not logged in.
        await Task.Run(() => _flash.Call("setBackgroundValues", sBG, customPath ?? string.Empty));

        if (localCustom)
            return ThemesStatus("immediate", false, true);

        bool loggedIn = false;
        try
        {
            loggedIn = _player.LoggedIn;
        }
        catch
        {
            // During the login/client bootstrap, treating the player as logged
            // out is the safe choice; the UI may reload only the renderer then.
        }

        return ThemesStatus("reload-required", !loggedIn, false);
    }

    private string? NormalizeCustomBackgroundPathForLinux(string? customPath)
    {
        if (string.IsNullOrWhiteSpace(customPath)) return customPath;

        string? baseUrl = Environment.GetEnvironmentVariable("SKUA_THEME_BASE_URL");
        if (string.IsNullOrWhiteSpace(baseUrl) ||
            !customPath.StartsWith("file:///", StringComparison.OrdinalIgnoreCase))
            return customPath;

        try
        {
            if (!Uri.TryCreate(customPath, UriKind.Absolute, out Uri? fileUri) || !fileUri.IsFile)
                return customPath;

            string localPath = Uri.UnescapeDataString(fileUri.LocalPath);
            if (!File.Exists(localPath)) return customPath;

            string migrated = BuildRuntimeBackgroundUrl(Path.GetFileName(localPath), localPath);
            _settings.Set<string?>("CustomBackgroundPath", migrated);
            return migrated;
        }
        catch
        {
            return customPath;
        }
    }

    private static string GetBackgroundFileName(string customPath)
    {
        if (Uri.TryCreate(customPath, UriKind.Absolute, out Uri? uri))
        {
            string fromUri = Path.GetFileName(Uri.UnescapeDataString(uri.LocalPath));
            if (!string.IsNullOrWhiteSpace(fromUri)) return fromUri;
        }

        return Path.GetFileName(
            customPath
                .Replace("file:///", string.Empty, StringComparison.OrdinalIgnoreCase)
                .Replace('/', Path.DirectorySeparatorChar));
    }

    private static string BuildRuntimeBackgroundUrl(string background, string localPath)
    {
        string? baseUrl = Environment.GetEnvironmentVariable("SKUA_THEME_BASE_URL");
        if (!string.IsNullOrWhiteSpace(baseUrl))
            return $"{baseUrl.TrimEnd('/')}/{Uri.EscapeDataString(background)}";

        return $"file:///{Path.GetFullPath(localPath).Replace('\\', '/')}";
    }

    private async Task<BridgeCommandResult> ThemesBackgroundImportAsync(BridgeCommand command)
    {
        string? source = ArgString(command, 0);
        if (string.IsNullOrWhiteSpace(source) || !File.Exists(source))
            return Fail("background-file-not-found");
        if (!string.Equals(Path.GetExtension(source), ".swf", StringComparison.OrdinalIgnoreCase))
            return Fail("background-must-be-swf");

        Directory.CreateDirectory(ClientFileSources.SkuaThemesDIR);
        string fileName = Path.GetFileName(source);
        string destination = Path.Combine(ClientFileSources.SkuaThemesDIR, fileName);
        if (!string.Equals(Path.GetFullPath(source), Path.GetFullPath(destination), StringComparison.Ordinal))
            File.Copy(source, destination, true);
        return await ThemesBackgroundSetAsync(
            new BridgeCommand(command.Id, command.Name,
                new[] { JsonSerializer.SerializeToElement(fileName) }));
    }

    private static object ParseTheme(string? serialized)
    {
        string value = serialized ?? string.Empty;
        string[] parts = value.Split(',', StringSplitOptions.TrimEntries);
        string Part(int index, string fallback) => index < parts.Length && !string.IsNullOrWhiteSpace(parts[index])
            ? parts[index]
            : fallback;
        return new
        {
            name = Part(0, "Unnamed"),
            baseTheme = NormalizeBaseTheme(Part(1, "Dark")),
            primary = NormalizeThemeColor(Part(2, "#FF607D8B"), "#FF607D8B"),
            secondary = NormalizeThemeColor(Part(3, "#FF607D8B"), "#FF607D8B"),
            primaryForeground = NormalizeThemeColor(Part(4, "#FF000000"), "#FF000000"),
            secondaryForeground = NormalizeThemeColor(Part(5, "#FF000000"), "#FF000000"),
            useColorAdjustment = bool.TryParse(Part(6, "false"), out bool adjusted) && adjusted,
            desiredContrastRatio = double.TryParse(Part(7, "4.5"), NumberStyles.Any, CultureInfo.InvariantCulture, out double ratio) ? ratio : 4.5,
            contrast = Part(8, "Medium"),
            colorSelection = Part(9, "All"),
            serialized = value
        };
    }

    private static string ParseThemeName(string? serialized) =>
        (serialized ?? string.Empty).Split(',', 2, StringSplitOptions.TrimEntries).FirstOrDefault() ?? string.Empty;

    private static string SerializeTheme(
        string name,
        string baseTheme,
        string primary,
        string secondary,
        string primaryForeground,
        string secondaryForeground,
        bool useAdjustment,
        double ratio,
        string contrast,
        string colorSelection)
    {
        string basePart = string.Join(',', new[]
        {
            name,
            NormalizeBaseTheme(baseTheme),
            NormalizeThemeColor(primary, "#FF607D8B"),
            NormalizeThemeColor(secondary, "#FF607D8B"),
            NormalizeThemeColor(primaryForeground, "#FF000000"),
            NormalizeThemeColor(secondaryForeground, "#FF000000")
        });
        if (!useAdjustment) return basePart;
        return string.Join(',', new[]
        {
            basePart,
            bool.TrueString,
            ratio.ToString(CultureInfo.InvariantCulture),
            contrast,
            colorSelection
        });
    }

    private static string NormalizeBaseTheme(string? value) =>
        string.Equals(value, "Light", StringComparison.OrdinalIgnoreCase) ? "Light" : "Dark";

    private static string NormalizeThemeColor(string? value, string fallback)
    {
        string text = (value ?? string.Empty).Trim();
        if (!text.StartsWith('#')) text = "#" + text;
        if (text.Length == 7) text = "#FF" + text[1..];
        return text.Length == 9 ? text.ToUpperInvariant() : fallback;
    }

    // ---------------------------------------------------------------------
    // Notify Drop
    // ---------------------------------------------------------------------

    private BridgeCommandResult NotifyStatus()
    {
        lock (_notifyLock)
        {
            return Ok(new
            {
                items = _notifyDrops.ToArray(),
                soundCount = _notifySoundCount,
                soundDelay = _notifySoundDelay,
                pending = _notifyEvents.Count
            });
        }
    }

    private BridgeCommandResult NotifyAdd(BridgeCommand command)
    {
        string? raw = ArgString(command, 0);
        if (string.IsNullOrWhiteSpace(raw)) return NotifyStatus();
        string[] values = raw.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        lock (_notifyLock)
        {
            foreach (string value in values)
                if (!_notifyDrops.Contains(value, StringComparer.Ordinal)) _notifyDrops.Add(value);
        }
        return NotifyStatus();
    }

    private BridgeCommandResult NotifyRemove(BridgeCommand command)
    {
        string[] values = ArgArray(command, 0)
            .Select(element => element.ValueKind == JsonValueKind.String ? element.GetString() : element.ToString())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Cast<string>()
            .ToArray();
        lock (_notifyLock)
            _notifyDrops.RemoveAll(value => values.Contains(value, StringComparer.Ordinal));
        return NotifyStatus();
    }

    private BridgeCommandResult NotifyClear()
    {
        lock (_notifyLock) _notifyDrops.Clear();
        while (_notifyEvents.TryDequeue(out _)) { }
        return NotifyStatus();
    }

    private BridgeCommandResult NotifyConfigure(BridgeCommand command)
    {
        lock (_notifyLock)
        {
            _notifySoundCount = Math.Max(1, ArgInt(command, 0, _notifySoundCount));
            _notifySoundDelay = Math.Max(0, ArgInt(command, 1, _notifySoundDelay));
        }
        return NotifyStatus();
    }

    private BridgeCommandResult NotifyTest()
    {
        lock (_notifyLock)
            return Ok(new { beep = true, soundCount = _notifySoundCount, soundDelay = _notifySoundDelay });
    }

    private BridgeCommandResult NotifyPoll()
    {
        List<NotifyDropEvent> events = new();
        while (_notifyEvents.TryDequeue(out NotifyDropEvent item)) events.Add(item);
        return Ok(new { events });
    }

    private void OnItemDropped(ItemDroppedMessage message)
    {
        int soundCount;
        int soundDelay;
        lock (_notifyLock)
        {
            if (!_notifyDrops.Contains(message.Item.Name, StringComparer.Ordinal)) return;
            soundCount = _notifySoundCount;
            soundDelay = _notifySoundDelay;
        }
        _notifyEvents.Enqueue(new(
            message.Item.Name,
            message.Item.ID,
            soundCount,
            soundDelay,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
    }

    private readonly record struct NotifyDropEvent(
        string Name,
        int ItemID,
        int SoundCount,
        int SoundDelay,
        long Timestamp);

    // ---------------------------------------------------------------------
    // Runtime helpers
    // ---------------------------------------------------------------------

    private BridgeCommandResult RuntimeDropsStatus() => Ok(new
    {
        enabled = _drops.Enabled,
        interval = _drops.Interval,
        rejectElse = _drops.RejectElse,
        acceptACDrops = _options.AcceptACDrops,
        items = _drops.ToPickup.Select(x => (object)x).Concat(_drops.ToPickupIDs.Select(x => (object)x)).ToArray()
    });

    private BridgeCommandResult RuntimeDropsAdd(BridgeCommand command)
    {
        string input = ArgString(command, 0) ?? string.Empty;
        string[] values = input.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        List<string> names = new();
        List<int> ids = new();
        foreach (string value in values)
        {
            if (int.TryParse(value, out int id)) ids.Add(id); else names.Add(value);
        }
        if (names.Count > 0) _drops.Add(names.ToArray());
        if (ids.Count > 0) _drops.Add(ids.ToArray());
        return RuntimeDropsStatus();
    }

    private BridgeCommandResult RuntimeDropsRemove(BridgeCommand command)
    {
        JsonElement[] values = ArgArray(command, 0);
        List<string> names = new();
        List<int> ids = new();
        foreach (JsonElement value in values)
        {
            string text = value.ToString();
            if (int.TryParse(text, out int id)) ids.Add(id); else names.Add(text);
        }
        if (names.Count > 0) _drops.Remove(names.ToArray());
        if (ids.Count > 0) _drops.Remove(ids.ToArray());
        return RuntimeDropsStatus();
    }

    private async Task<BridgeCommandResult> RuntimeDropsToggleAsync()
    {
        if (_drops.Enabled) await _drops.StopAsync(); else _drops.Start();
        return RuntimeDropsStatus();
    }

    private BridgeCommandResult RuntimeDropsConfigure(BridgeCommand command)
    {
        if (command.Arguments.Count > 0) _drops.Interval = Math.Max(0, ArgInt(command, 0, _drops.Interval));
        if (command.Arguments.Count > 1) _drops.RejectElse = ArgBool(command, 1, _drops.RejectElse);
        if (command.Arguments.Count > 2) _options.AcceptACDrops = ArgBool(command, 2, _options.AcceptACDrops);
        return RuntimeDropsStatus();
    }

    private BridgeCommandResult RuntimeQuestsStatus() => Ok(new
    {
        quests = _quests.Registered.Select(id => new
        {
            questId = id,
            rewardId = _quests.RegisteredRewards.TryGetValue(id, out int rewardId) ? rewardId : -1
        }).ToArray()
    });

    private BridgeCommandResult RuntimeQuestsAdd(BridgeCommand command)
    {
        string input = ArgString(command, 0) ?? string.Empty;
        int rewardId = ArgInt(command, 1, -1);
        string[] values = input.Split(new[] { '|', ',', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        List<(int, int)> quests = new();
        foreach (string value in values)
            if (int.TryParse(value, out int id)) quests.Add((id, rewardId));
        if (quests.Count > 0) _quests.RegisterQuests(quests.ToArray());
        return RuntimeQuestsStatus();
    }

    private BridgeCommandResult RuntimeQuestsRemove(BridgeCommand command)
    {
        int[] ids = ArgArray(command, 0).Select(x => int.TryParse(x.ToString(), out int id) ? id : -1).Where(x => x >= 0).ToArray();
        if (ids.Length > 0) _quests.UnregisterQuests(ids);
        return RuntimeQuestsStatus();
    }

    private BridgeCommandResult RuntimeBoostsStatus() => Ok(new
    {
        enabled = _boosts.Enabled,
        classBoostID = _boosts.ClassBoostID,
        experienceBoostID = _boosts.ExperienceBoostID,
        goldBoostID = _boosts.GoldBoostID,
        reputationBoostID = _boosts.ReputationBoostID,
        useClassBoost = _boosts.UseClassBoost,
        useExperienceBoost = _boosts.UseExperienceBoost,
        useGoldBoost = _boosts.UseGoldBoost,
        useReputationBoost = _boosts.UseReputationBoost
    });

    private BridgeCommandResult RuntimeBoostsSet(BridgeCommand command)
    {
        string? name = ArgString(command, 0);
        switch (name)
        {
            case nameof(IScriptBoost.ClassBoostID): _boosts.ClassBoostID = ArgInt(command, 1, _boosts.ClassBoostID); break;
            case nameof(IScriptBoost.ExperienceBoostID): _boosts.ExperienceBoostID = ArgInt(command, 1, _boosts.ExperienceBoostID); break;
            case nameof(IScriptBoost.GoldBoostID): _boosts.GoldBoostID = ArgInt(command, 1, _boosts.GoldBoostID); break;
            case nameof(IScriptBoost.ReputationBoostID): _boosts.ReputationBoostID = ArgInt(command, 1, _boosts.ReputationBoostID); break;
            case nameof(IScriptBoost.UseClassBoost): _boosts.UseClassBoost = ArgBool(command, 1, _boosts.UseClassBoost); break;
            case nameof(IScriptBoost.UseExperienceBoost): _boosts.UseExperienceBoost = ArgBool(command, 1, _boosts.UseExperienceBoost); break;
            case nameof(IScriptBoost.UseGoldBoost): _boosts.UseGoldBoost = ArgBool(command, 1, _boosts.UseGoldBoost); break;
            case nameof(IScriptBoost.UseReputationBoost): _boosts.UseReputationBoost = ArgBool(command, 1, _boosts.UseReputationBoost); break;
            default: return Fail("boost-option-not-found");
        }
        return RuntimeBoostsStatus();
    }

    private async Task<BridgeCommandResult> RuntimeBoostsToggleAsync()
    {
        if (_boosts.Enabled) await _boosts.StopAsync(); else _boosts.Start();
        return RuntimeBoostsStatus();
    }

    private BridgeCommandResult RuntimeBoostsDetect(BridgeCommand command)
    {
        bool searchBank = ArgBool(command, 0, false);
        _boosts.SetAllBoostsIDs(searchBank);
        return RuntimeBoostsStatus();
    }

    // ---------------------------------------------------------------------
    // Fast Travel (same FastTravels setting as original)
    // ---------------------------------------------------------------------

    private List<TravelEntry> ReadTravels()
    {
        StringCollection? values = _settings.Get<StringCollection>("FastTravels");
        List<TravelEntry> list = new();
        if (values is null) return list;
        foreach (string? line in values)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            string[] parts = line.Split(',', 4);
            if (parts.Length != 4) continue;
            list.Add(new(parts[0], parts[1], parts[2], parts[3]));
        }
        return list;
    }

    private void SaveTravels(IEnumerable<TravelEntry> entries)
    {
        StringCollection values = new();
        foreach (TravelEntry entry in entries)
            values.Add($"{entry.Description},{entry.Map},{entry.Cell},{entry.Pad}");
        _settings.Set("FastTravels", values);
    }

    private BridgeCommandResult TravelList() => Ok(new
    {
        usePrivateRoom = _mapService.UsePrivateRoom,
        privateRoomNumber = _mapService.PrivateRoomNumber,
        items = ReadTravels().Select((x, i) => new { index = i, descriptionName = x.Description, mapName = x.Map, cell = x.Cell, pad = x.Pad }).ToArray()
    });

    private BridgeCommandResult TravelCurrent()
    {
        var current = _mapService.GetCurrentLocation();
        return Ok(new { mapName = current.mapName, cell = current.cell, pad = current.pad });
    }

    private BridgeCommandResult TravelAdd(BridgeCommand command)
    {
        TravelEntry? entry = TravelEntryFromArgs(command, 0);
        if (entry is null) return Fail("invalid-fast-travel");
        List<TravelEntry> items = ReadTravels();
        items.Add(entry.Value);
        SaveTravels(items);
        return TravelList();
    }

    private BridgeCommandResult TravelUpdate(BridgeCommand command)
    {
        int index = ArgInt(command, 0, -1);
        TravelEntry? entry = TravelEntryFromArgs(command, 1);
        List<TravelEntry> items = ReadTravels();
        if (index < 0 || index >= items.Count || entry is null) return Fail("invalid-fast-travel");
        items[index] = entry.Value;
        SaveTravels(items);
        return TravelList();
    }

    private BridgeCommandResult TravelRemove(BridgeCommand command)
    {
        int index = ArgInt(command, 0, -1);
        List<TravelEntry> items = ReadTravels();
        if (index < 0 || index >= items.Count) return Fail("fast-travel-not-found");
        items.RemoveAt(index);
        SaveTravels(items);
        return TravelList();
    }

    private BridgeCommandResult TravelClear()
    {
        SaveTravels(Array.Empty<TravelEntry>());
        return TravelList();
    }

    private BridgeCommandResult TravelGo(BridgeCommand command)
    {
        int index = ArgInt(command, 0, -1);
        List<TravelEntry> items = ReadTravels();
        if (index < 0 || index >= items.Count) return Fail("fast-travel-not-found");
        TravelEntry entry = items[index];
        FastTravelItemViewModel vm = new(
            entry.Description,
            entry.Map,
            entry.Cell,
            entry.Pad,
            new RelayCommand<object>(_ => { }));
        _mapService.Travel(vm);
        return Ok(new { started = true, index });
    }

    private BridgeCommandResult TravelSettings(BridgeCommand command)
    {
        _mapService.UsePrivateRoom = ArgBool(command, 0, _mapService.UsePrivateRoom);
        _mapService.PrivateRoomNumber = ArgInt(command, 1, _mapService.PrivateRoomNumber);
        return TravelList();
    }

    private static TravelEntry? TravelEntryFromArgs(BridgeCommand command, int offset)
    {
        string? description = ArgString(command, offset);
        string? map = ArgString(command, offset + 1);
        string? cell = ArgString(command, offset + 2);
        string? pad = ArgString(command, offset + 3);
        if (string.IsNullOrWhiteSpace(description) || string.IsNullOrWhiteSpace(map) || string.IsNullOrWhiteSpace(cell) || string.IsNullOrWhiteSpace(pad)) return null;
        return new(description, map, cell, pad);
    }

    private readonly record struct TravelEntry(string Description, string Map, string Cell, string Pad);

    // ---------------------------------------------------------------------
    // Loader
    // ---------------------------------------------------------------------

    private BridgeCommandResult LoaderStatus()
    {
        lock (_loaderLock)
        {
            return Ok(new
            {
                isLoading = _loaderIsLoading,
                progress = _loaderProgress,
                quests = _loaderQuestData.Select(q => new { id = q.ID, name = q.Name }).ToArray()
            });
        }
    }

    private BridgeCommandResult LoaderLoad(BridgeCommand command)
    {
        int selectedIndex = ArgInt(command, 0, 0);
        string input = ArgString(command, 1) ?? string.Empty;
        if (selectedIndex == 0)
        {
            if (!int.TryParse(input.Trim(), out int shopId)) return Fail("invalid-shop-id");
            _ = Task.Run(() => _shops.Load(shopId));
            return Ok(new { started = true, type = "shop", id = shopId });
        }

        int[] ids = ParseIds(input, new[] { ',', ' ' }) ?? Array.Empty<int>();
        if (ids.Length == 0) return Fail("quest-ids-required");
        _ = Task.Run(() => _quests.Load(ids));
        return Ok(new { started = true, type = "quest", ids });
    }

    private async Task<BridgeCommandResult> LoaderGetQuestsAsync()
    {
        List<QuestData> data = await _questLoader.GetFromFileAsync("QuestData.json");
        lock (_loaderLock)
        {
            _loaderQuestData = data;
            _loaderProgress = string.Empty;
        }
        return LoaderStatus();
    }

    private BridgeCommandResult LoaderUpdate(BridgeCommand command)
    {
        bool getAll = ArgBool(command, 0, false);
        return StartLoaderTask(async (progress, token) =>
            await _questLoader.UpdateAsync("QuestData.json", getAll, progress, token));
    }

    private BridgeCommandResult LoaderUpdateRange(BridgeCommand command)
    {
        int startId = ArgInt(command, 0, -1);
        int endId = ArgInt(command, 1, -1);
        if (startId < 0 || endId < 0) return Fail("invalid-range");
        if (startId > endId) return Fail("range-start-greater-than-end");
        return StartLoaderTask(async (progress, token) =>
            await _questLoader.UpdateRangeAsync("QuestData.json", startId, endId, progress, token));
    }

    private BridgeCommandResult StartLoaderTask(
        Func<IProgress<string>, CancellationToken, Task<List<QuestData>>> work)
    {
        lock (_loaderLock)
        {
            if (_loaderTask is { IsCompleted: false }) return LoaderStatus();
            _loaderCts?.Dispose();
            _loaderCts = new CancellationTokenSource();
            CancellationToken token = _loaderCts.Token;
            _loaderIsLoading = true;
            _loaderProgress = "Working...";
            Progress<string> progress = new(value =>
            {
                lock (_loaderLock)
                {
                    _loaderIsLoading = true;
                    _loaderProgress = value;
                }
            });
            _loaderTask = Task.Run(async () =>
            {
                try
                {
                    List<QuestData> data = await work(progress, token);
                    lock (_loaderLock) _loaderQuestData = data;
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    lock (_loaderLock) _loaderProgress = "Cancelled.";
                }
                catch (Exception exception)
                {
                    lock (_loaderLock) _loaderProgress = exception.Message;
                }
                finally
                {
                    lock (_loaderLock) _loaderIsLoading = false;
                }
            }, CancellationToken.None);
        }
        return LoaderStatus();
    }

    private BridgeCommandResult LoaderCancel()
    {
        lock (_loaderLock)
        {
            _loaderProgress = "Cancelling task...";
            _loaderCts?.Cancel();
        }
        return LoaderStatus();
    }

    private BridgeCommandResult LoaderFakeComplete(BridgeCommand command)
    {
        int id = ArgInt(command, 0, -1);
        if (id < 0) return Fail("quest-id-required");
        bool result = _quests.UpdateQuest(id);
        return Ok(new { id, completed = result });
    }

    // ---------------------------------------------------------------------
    // Grabber
    // ---------------------------------------------------------------------

    private BridgeCommandResult GrabberStatus(BridgeCommand command)
    {
        string? typeText = ArgString(command, 0);
        if (!Enum.TryParse(typeText?.Replace(' ', '_'), true, out GrabberTypes type))
            return Fail("invalid-grabber-type");
        List<object> items = _grabber.Grab(type);
        return Ok(new
        {
            type = type.ToString(),
            items = items.Select((item, index) => DescribeGrabberItem(item, index)).ToArray()
        });
    }

    private static object DescribeGrabberItem(object item, int index)
    {
        return item switch
        {
            ShopItem shopItem => new
            {
                index,
                kind = "ShopItem",
                id = shopItem.ID,
                name = shopItem.Name,
                shopItemId = shopItem.ShopItemID,
                cost = shopItem.Cost,
                coins = shopItem.Coins,
                maxStack = shopItem.MaxStack,
                quantity = shopItem.Quantity,
                category = shopItem.Category.ToString()
            },
            ShopInfo shop => new { index, kind = "ShopInfo", id = shop.ID, name = shop.Name },
            Quest quest => new { index, kind = "Quest", id = quest.ID, name = quest.Name },
            InventoryItem inventory => new
            {
                index,
                kind = "InventoryItem",
                id = inventory.ID,
                name = inventory.Name,
                quantity = inventory.Quantity,
                category = inventory.Category.ToString(),
                equipped = inventory.Equipped,
                coins = inventory.Coins
            },
            Monster monster => new
            {
                index,
                kind = "Monster",
                id = monster.MapID,
                monsterId = monster.ID,
                name = monster.Name,
                cell = monster.Cell,
                hp = monster.HP
            },
            MapItem mapItem => new
            {
                index,
                kind = "MapItem",
                id = mapItem.ID,
                questId = mapItem.QuestID,
                name = $"Map Item {mapItem.ID}"
            },
            _ => new
            {
                index,
                kind = item.GetType().Name,
                id = GetIntProperty(item, "ID"),
                name = GetProperty(item, "Name")?.ToString() ?? item.ToString() ?? string.Empty
            }
        };
    }

    private async Task<BridgeCommandResult> GrabberActionAsync(
        BridgeCommand command,
        CancellationToken cancellationToken)
    {
        string? typeText = ArgString(command, 0);
        string action = ArgString(command, 1) ?? string.Empty;
        if (!Enum.TryParse(typeText?.Replace(' ', '_'), true, out GrabberTypes type))
            return Fail("invalid-grabber-type");
        List<object> source = _grabber.Grab(type);
        int[] indices = ArgArray(command, 2)
            .Select(value => value.TryGetInt32(out int index) ? index : -1)
            .Where(index => index >= 0 && index < source.Count)
            .Distinct()
            .ToArray();
        List<object> selected = indices.Select(index => source[index]).ToList();
        int quantity = ArgInt(command, 3, 1);

        if (action == "Unregister All")
        {
            _quests.UnregisterAllQuests();
            return Ok(new { message = "Finished." });
        }
        if (selected.Count == 0) return Fail("nothing-selected");

        switch (type)
        {
            case GrabberTypes.Shop_Items:
                if (action != "Buy") return Fail("unsupported-action");
                return await GrabberBuyAsync(selected.Cast<ShopItem>().ToList(), quantity, cancellationToken);

            case GrabberTypes.Shop_IDs:
                if (action != "Load Shop") return Fail("unsupported-action");
                ShopInfo shop = (ShopInfo)selected[0];
                await Task.Run(() => _shops.Load(shop.ID), cancellationToken);
                return Ok(new { message = $"Shop {shop.Name} [{shop.ID}] loaded." });

            case GrabberTypes.Quests:
                return await GrabberQuestActionAsync(selected.Cast<Quest>().Select(q => q.ID).ToArray(), action, cancellationToken);

            case GrabberTypes.Inventory_Items:
                return await GrabberInventoryActionAsync(selected.Cast<InventoryItem>().ToList(), action, quantity, cancellationToken);

            case GrabberTypes.House_Inventory_Items:
                if (action != "To Bank") return Fail("unsupported-action");
                await RunItemSequenceAsync(selected.Cast<InventoryItem>().ToList(), item => _houseInventory.ToBank(item.ID), cancellationToken);
                return Ok(new { message = "Finished." });

            case GrabberTypes.Temp_Inventory_Items:
                return Fail("no-actions-for-temp-inventory");

            case GrabberTypes.Bank_Items:
                if (action != "To Inventory") return Fail("unsupported-action");
                await RunItemSequenceAsync(selected.Cast<InventoryItem>().ToList(), item => _bank.ToInventory(item.ID), cancellationToken);
                return Ok(new { message = "Finished." });

            case GrabberTypes.Cell_Monsters:
            case GrabberTypes.Map_Monsters:
                if (action == "Teleport To")
                {
                    Monster monster = (Monster)selected[0];
                    await Task.Run(() => _map.Jump(monster.Cell, "Left"), cancellationToken);
                    return Ok(new { message = $"Teleported to {monster.Name}." });
                }
                if (action == "Kill")
                {
                    foreach (Monster monster in selected.Cast<Monster>())
                    {
                        if (monster.Cell != _player.Cell) _map.Jump(monster.Cell, "Left");
                        _kill.Monster(monster, cancellationToken);
                        await Task.Delay(1000, cancellationToken);
                    }
                    return Ok(new { message = "Finished." });
                }
                return Fail("unsupported-action");

            case GrabberTypes.GetMap_Item_IDs:
                return await GrabberMapItemActionAsync(selected.Cast<MapItem>().ToList(), action, quantity, cancellationToken);

            default:
                return Fail("unsupported-grabber-type");
        }
    }

    private async Task<BridgeCommandResult> GrabberBuyAsync(
        List<ShopItem> items,
        int quantity,
        CancellationToken cancellationToken)
    {
        if (items.Any(item => item.Coins && item.Cost > 0))
            return Fail("Don't use this to buy AC items that aren't 0 AC.");

        if (items.Count == 1)
        {
            ShopItem item = items[0];
            int finalQuantity = Math.Max(1, Math.Min(quantity, item.MaxStack));
            int totalCost = item.Cost * finalQuantity;
            if (!item.Coins && totalCost > _player.Gold)
                return Fail($"Not enough gold. Total: {totalCost:#,0}; Needed: {totalCost - _player.Gold:#,0}");
            await Task.Run(() => _shops.BuyItem(item.ID, item.ShopItemID, finalQuantity), cancellationToken);
            return Ok(new { message = $"Bought {finalQuantity} {item.Name}" });
        }

        int totalGoldCost = items.Where(item => !item.Coins).Sum(item => item.Cost);
        if (totalGoldCost > _player.Gold)
            return Fail($"Not enough gold. Total: {totalGoldCost:#,0}; Needed: {totalGoldCost - _player.Gold:#,0}");
        for (int index = 0; index < items.Count; index++)
        {
            ShopItem item = items[index];
            await Task.Run(() => _shops.BuyItem(item.ID), cancellationToken);
            if (index != items.Count - 1) await Task.Delay(1000, cancellationToken);
        }
        return Ok(new { message = "Finished." });
    }

    private async Task<BridgeCommandResult> GrabberQuestActionAsync(
        int[] ids,
        string action,
        CancellationToken cancellationToken)
    {
        switch (action)
        {
            case "Open": await Task.Run(() => _quests.Load(ids), cancellationToken); break;
            case "Accept": await Task.Run(() => _quests.EnsureAccept(ids), cancellationToken); break;
            case "Register": await Task.Run(() => _quests.RegisterQuests(ids), cancellationToken); break;
            case "Fake Complete":
                if (ids.Length != 1) return Fail("Please select exactly one quest to complete.");
                await Task.Run(() => _quests.UpdateQuest(ids[0]), cancellationToken);
                break;
            default: return Fail("unsupported-action");
        }
        return Ok(new { message = "Finished." });
    }

    private async Task<BridgeCommandResult> GrabberInventoryActionAsync(
        List<InventoryItem> items,
        string action,
        int quantity,
        CancellationToken cancellationToken)
    {
        if (action is "Sell" or "Sell All")
        {
            if (items.Count != 1) return Fail("Please sell 1 item at a time to prevent losses.");
            InventoryItem item = items[0];
            if (item.Equipped) return Fail("Cannot sell equipped item.");
            int finalQuantity = action == "Sell All"
                ? (item.Category == ItemCategory.Class ? 1 : item.Quantity)
                : Math.Max(1, quantity);
            await Task.Run(() => _shops.SellItem(item.ID, finalQuantity), cancellationToken);
            return Ok(new { message = $"Sold {finalQuantity} {item.Name}" });
        }

        Action<InventoryItem>? operation = action switch
        {
            "Equip" => item => _inventory.EquipItem(item.ID),
            "To Bank" => item => _inventory.ToBank(item.ID),
            _ => null
        };
        if (operation is null) return Fail("unsupported-action");
        await RunItemSequenceAsync(items, operation, cancellationToken);
        return Ok(new { message = "Finished." });
    }

    private async Task<BridgeCommandResult> GrabberMapItemActionAsync(
        List<MapItem> items,
        string action,
        int quantity,
        CancellationToken cancellationToken)
    {
        int[] questIds = items.Select(item => item.QuestID).ToArray();
        switch (action)
        {
            case "Open": await Task.Run(() => _quests.Load(questIds), cancellationToken); break;
            case "Accept": await Task.Run(() => _quests.EnsureAccept(questIds), cancellationToken); break;
            case "Get Map Item":
                int count = Math.Max(1, quantity);
                for (int index = 0; index < items.Count; index++)
                {
                    MapItem item = items[index];
                    await Task.Run(() => _map.GetMapItem(item.ID, count), cancellationToken);
                    if (index != items.Count - 1) await Task.Delay(1000, cancellationToken);
                }
                break;
            case "Fake Complete":
                if (questIds.Length != 1) return Fail("Please select exactly one quest to complete.");
                await Task.Run(() => _quests.UpdateQuest(questIds[0]), cancellationToken);
                break;
            default: return Fail("unsupported-action");
        }
        return Ok(new { message = "Finished." });
    }

    private static async Task RunItemSequenceAsync(
        List<InventoryItem> items,
        Action<InventoryItem> action,
        CancellationToken cancellationToken)
    {
        for (int index = 0; index < items.Count; index++)
        {
            action(items[index]);
            if (index != items.Count - 1) await Task.Delay(1000, cancellationToken);
        }
    }

    // ---------------------------------------------------------------------
    // Current drops / Stats / Console / Logs
    // ---------------------------------------------------------------------

    private BridgeCommandResult CurrentDropsStatus() => Ok(new
    {
        drops = _drops.CurrentDropInfos.Select(DescribeItem).ToArray()
    });

    private BridgeCommandResult CurrentDropsPick(BridgeCommand command)
    {
        int[] ids = ArgArray(command, 0).Select(x => int.TryParse(x.ToString(), out int id) ? id : -1).Where(x => x >= 0).ToArray();
        if (ids.Length == 0 && command.Arguments.Count > 0)
        {
            int id = ArgInt(command, 0, -1);
            if (id >= 0) ids = new[] { id };
        }
        if (ids.Length > 0 && _player.Playing) _drops.Pickup(ids);
        return CurrentDropsStatus();
    }

    private BridgeCommandResult StatsStatus()
    {
        object? time = _stats.GetType().GetProperty("Time", BindingFlags.Instance | BindingFlags.Public)?.GetValue(_stats);
        return Ok(new
        {
            _stats.Kills,
            _stats.Deaths,
            _stats.QuestsAccepted,
            _stats.QuestsCompleted,
            _stats.Drops,
            _stats.Relogins,
            time = time?.ToString() ?? string.Empty,
            inventory = new { max = _stats.InventorySpace, used = _stats.InventoryFilledSpace, free = _stats.InventoryFreeSpace },
            bank = new { max = _stats.BankSpace, used = _stats.BankFilledSpace, free = _stats.BankFreeSpace }
        });
    }

    private BridgeCommandResult ConsoleRun(BridgeCommand command)
    {
        string snippet = ArgString(command, 0) ?? string.Empty;
        if (string.IsNullOrWhiteSpace(snippet)) return Fail("snippet-required");
        const string source =
            "using Skua.Core;\n" +
            "using Skua.Core.Interfaces;\n" +
            "using Skua.Core.Utils;\n" +
            "using Skua.Core.Models;\n" +
            "using Skua.Core.Models.Auras;\n" +
            "using Skua.Core.Models.Items;\n" +
            "using Skua.Core.Models.Monsters;\n" +
            "using Skua.Core.Models.Players;\n" +
            "using Skua.Core.Models.Quests;\n" +
            "using Skua.Core.Models.Servers;\n" +
            "using Skua.Core.Models.Shops;\n" +
            "using Skua.Core.Models.Skills;\n" +
            "using Newtonsoft.Json;\n" +
            "public class Script{ public void ScriptMain(IScriptInterface Bot){";
        try
        {
            object? compiled = _scriptManager.Compile($"{source}{snippet}\n}}}}");
            if (compiled is null) return Fail("compile-returned-null");
            compiled.GetType().GetMethod("ScriptMain")!.Invoke(compiled, new object[] { IScriptInterface.Instance });
            return Ok(new { executed = true });
        }
        catch (Exception exception)
        {
            Exception actual = exception.InnerException ?? exception;
            return Fail($"{actual.Message}\n{actual.StackTrace}");
        }
    }

    private BridgeCommandResult LogsStatus(BridgeCommand command)
    {
        string typeText = ArgString(command, 0) ?? "Script";
        if (!Enum.TryParse(typeText, true, out LogType type)) return Fail("invalid-log-type");
        return Ok(new { type = type.ToString(), logs = _logs.GetLogs(type).ToArray() });
    }

    private BridgeCommandResult LogsClear(BridgeCommand command)
    {
        string typeText = ArgString(command, 0) ?? "Script";
        if (!Enum.TryParse(typeText, true, out LogType type)) return Fail("invalid-log-type");
        _logs.ClearLog(type);
        return LogsStatus(command);
    }

    // ---------------------------------------------------------------------
    // Auto (same IScriptAuto methods as original AutoViewModel)
    // ---------------------------------------------------------------------

    private BridgeCommandResult AutoStatus()
    {
        List<string> classes = _inventory.Items?
            .Where(item => item.Category.ToString() == "Class")
            .Select(item => item.Name)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList() ?? new List<string>();
        Dictionary<string, List<string>> modes = _advancedSkills.GetAvailableClassModes();
        return Ok(new
        {
            running = _auto.IsRunning,
            classes,
            modes,
            currentClass = _player.CurrentClass?.Name ?? string.Empty
        });
    }

    private BridgeCommandResult AutoEquip(BridgeCommand command)
    {
        string? className = ArgString(command, 0);
        if (!string.IsNullOrWhiteSpace(className)) _inventory.EquipItem(className);
        return AutoStatus();
    }

    private BridgeCommandResult AutoStart(BridgeCommand command, bool hunt)
    {
        string? className = ArgString(command, 0);
        string? modeString = ArgString(command, 1);
        string? manualText = ArgString(command, 2);
        ClassUseMode classMode = ClassUseMode.Base;
        if (!string.IsNullOrWhiteSpace(className) && !string.IsNullOrWhiteSpace(modeString))
        {
            AdvancedSkill? skill = _advancedSkills.GetClassModeSkills(className, modeString);
            if (skill is not null) classMode = skill.ClassUseMode;
            else Enum.TryParse(modeString, true, out classMode);
        }
        int[]? manualIds = ParseIds(manualText, new[] { ',', ' ', ';' });
        if (hunt) _auto.StartAutoHunt(string.IsNullOrWhiteSpace(className) ? null : className, classMode, manualIds);
        else _auto.StartAutoAttack(string.IsNullOrWhiteSpace(className) ? null : className, classMode, manualIds);
        return Ok(new { started = true, mode = hunt ? "hunt" : "attack", className, classUseMode = classMode.ToString(), manualMapIDs = manualIds });
    }

    private async Task<BridgeCommandResult> AutoStopAsync()
    {
        await _auto.StopAsync();
        return AutoStatus();
    }

    // ---------------------------------------------------------------------
    // Advanced skills
    // ---------------------------------------------------------------------

    private BridgeCommandResult SkillsStatus()
    {
        return Ok(new
        {
            classUseModes = Enum.GetNames<ClassUseMode>(),
            skillUseModes = Enum.GetNames<SkillUseMode>(),
            skills = _advancedSkills.LoadedSkills.Select(skill => new
            {
                skill.ClassName,
                skill.Skills,
                skill.SkillTimeout,
                classUseMode = skill.ClassUseMode.ToString(),
                skillUseMode = skill.SkillUseMode.ToString(),
                skill.ResetComboOnTargetChange,
                skill.SaveString,
                display = skill.ToString()
            }).ToArray(),
            availableModes = _advancedSkills.GetAvailableClassModes()
        });
    }

    private BridgeCommandResult SkillsSave(BridgeCommand command)
    {
        string? className = ArgString(command, 0);
        string skills = ArgString(command, 1) ?? string.Empty;
        int timeout = ArgInt(command, 2, 100);
        string classMode = ArgString(command, 3) ?? "Base";
        string useMode = ArgString(command, 4) ?? "UseIfAvailable";
        bool reset = ArgBool(command, 5, false);
        if (string.IsNullOrWhiteSpace(className) || string.IsNullOrWhiteSpace(skills)) return Fail("class-and-skills-required");
        AdvancedSkill skill = new(className, skills, timeout, classMode, useMode, reset);
        _advancedSkills.TryOverride(skill);
        _advancedSkills.Save();
        return SkillsStatus();
    }

    private BridgeCommandResult SkillsRemove(BridgeCommand command)
    {
        string? className = ArgString(command, 0);
        string mode = ArgString(command, 1) ?? "Base";
        AdvancedSkill? skill = _advancedSkills.LoadedSkills.FirstOrDefault(x => x.ClassName == className && x.ClassUseMode.ToString().Equals(mode, StringComparison.OrdinalIgnoreCase));
        if (skill is null) return Fail("skill-set-not-found");
        _advancedSkills.Remove(skill);
        _advancedSkills.Save();
        return SkillsStatus();
    }

    private BridgeCommandResult SkillsParse(BridgeCommand command)
    {
        string text = ArgString(command, 0) ?? string.Empty;
        if (string.IsNullOrWhiteSpace(text)) return Fail("skill-string-required");
        SkillItemViewModel item = new(text.Trim());
        SkillRulesViewModel rules = item.UseRules;
        return Ok(new
        {
            skill = item.Skill,
            rules = DescribeRules(rules),
            display = item.ToString(),
            converted = item.Convert()
        });
    }

    private BridgeCommandResult SkillsBuild(BridgeCommand command)
    {
        int skillId = ArgInt(command, 0, -1);
        if (skillId < 0) return Fail("skill-id-required");
        SkillRulesViewModel rules = new()
        {
            UseRuleBool = ArgBool(command, 1, false),
            WaitUseValue = ArgInt(command, 2, 0),
            HealthGreaterThanBool = ArgBool(command, 3, true),
            HealthUseValue = Math.Max(0, ArgInt(command, 4, 0)),
            HealthIsPercentage = ArgBool(command, 5, true),
            ManaGreaterThanBool = ArgBool(command, 6, true),
            ManaUseValue = Math.Max(0, ArgInt(command, 7, 0)),
            ManaIsPercentage = ArgBool(command, 8, true),
            AuraGreaterThanBool = ArgBool(command, 9, true),
            AuraUseValue = ArgFloat(command, 10, 0),
            AuraTargetIndex = ArgInt(command, 11, 0),
            AuraName = ArgString(command, 12) ?? string.Empty,
            SkipUseBool = ArgBool(command, 13, false),
            PartyMemberHealthGreaterThanBool = ArgBool(command, 14, true),
            PartyMemberHealthUseValue = Math.Max(0, ArgInt(command, 15, 0)),
            PartyMemberHealthIsPercentage = ArgBool(command, 16, true),
            MultiAuraBool = ArgBool(command, 17, false),
            MultiAuraOperatorIndex = ArgInt(command, 18, 0)
        };
        if (command.Arguments.Count > 19 && command.Arguments[19].ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement aura in command.Arguments[19].EnumerateArray())
            {
                string name = aura.TryGetProperty("auraName", out JsonElement nameElement)
                    ? nameElement.GetString() ?? string.Empty
                    : string.Empty;
                float stackCount = aura.TryGetProperty("stackCount", out JsonElement stackElement) && stackElement.TryGetSingle(out float parsedStack)
                    ? parsedStack
                    : 0;
                bool isGreater = !aura.TryGetProperty("isGreater", out JsonElement greaterElement) || greaterElement.ValueKind != JsonValueKind.False;
                int targetIndex = aura.TryGetProperty("auraTargetIndex", out JsonElement targetElement) && targetElement.TryGetInt32(out int parsedTarget)
                    ? parsedTarget
                    : 0;
                if (!string.IsNullOrWhiteSpace(name))
                {
                    rules.MultiAuraChecks.Add(new AuraCheckViewModel
                    {
                        AuraName = name,
                        StackCount = stackCount,
                        IsGreater = isGreater,
                        AuraTargetIndex = targetIndex
                    });
                }
            }
        }
        SkillItemViewModel item = new(skillId, rules);
        return Ok(new { converted = item.Convert(), display = item.ToString() });
    }

    private static object DescribeRules(SkillRulesViewModel rules) => new
    {
        rules.UseRuleBool,
        rules.WaitUseValue,
        rules.HealthGreaterThanBool,
        rules.HealthUseValue,
        rules.HealthIsPercentage,
        rules.ManaGreaterThanBool,
        rules.ManaUseValue,
        rules.ManaIsPercentage,
        rules.AuraGreaterThanBool,
        rules.AuraUseValue,
        rules.AuraTargetIndex,
        rules.AuraName,
        rules.SkipUseBool,
        rules.PartyMemberHealthGreaterThanBool,
        rules.PartyMemberHealthUseValue,
        rules.PartyMemberHealthIsPercentage,
        rules.MultiAuraBool,
        rules.MultiAuraOperatorIndex,
        multiAuraChecks = rules.MultiAuraChecks.Select(x => new { x.AuraName, x.StackCount, x.IsGreater, x.AuraTargetIndex }).ToArray()
    };

    // ---------------------------------------------------------------------
    // Packet Spammer / Logger
    // ---------------------------------------------------------------------

    private BridgeCommandResult PacketSpammerStatus()
    {
        lock (_spammerLock)
        {
            return Ok(new
            {
                running = _spammerTask is { IsCompleted: false },
                sendToClient = _spammerSendToClient,
                spamDelay = _spammerDelay,
                selectedIndex = _spammerSelectedIndex,
                packets = _spammerPackets.ToArray()
            });
        }
    }

    private BridgeCommandResult PacketSpammerConfigure(BridgeCommand command)
    {
        lock (_spammerLock)
        {
            _spammerSendToClient = ArgBool(command, 0, _spammerSendToClient);
            _spammerDelay = Math.Max(1, ArgInt(command, 1, _spammerDelay));
        }
        return PacketSpammerStatus();
    }

    private BridgeCommandResult PacketSpammerAdd(BridgeCommand command)
    {
        string? packet = ArgString(command, 0);
        if (string.IsNullOrWhiteSpace(packet)) return Fail("packet-required");
        lock (_spammerLock) _spammerPackets.Add(packet);
        return PacketSpammerStatus();
    }

    private BridgeCommandResult PacketSpammerRemove(BridgeCommand command)
    {
        int index = ArgInt(command, 0, -1);
        lock (_spammerLock)
        {
            if (index >= 0 && index < _spammerPackets.Count) _spammerPackets.RemoveAt(index);
        }
        return PacketSpammerStatus();
    }

    private BridgeCommandResult PacketSpammerClear()
    {
        lock (_spammerLock) _spammerPackets.Clear();
        return PacketSpammerStatus();
    }

    private BridgeCommandResult PacketSpammerSend(BridgeCommand command)
    {
        string? packet = ArgString(command, 0);
        bool sendToClient = command.Arguments.Count > 1 ? ArgBool(command, 1, _spammerSendToClient) : _spammerSendToClient;
        if (string.IsNullOrWhiteSpace(packet)) return Fail("packet-required");
        SendPacket(packet, sendToClient);
        return Ok(new { sent = true });
    }

    private BridgeCommandResult PacketSpammerStart()
    {
        lock (_spammerLock)
        {
            if (_spammerTask is { IsCompleted: false }) return PacketSpammerStatus();
            _spammerCts = new CancellationTokenSource();
            CancellationToken token = _spammerCts.Token;
            _spammerTask = Task.Run(async () =>
            {
                try
                {
                    while (!token.IsCancellationRequested)
                    {
                        string[] packets;
                        bool sendToClient;
                        int delay;
                        lock (_spammerLock)
                        {
                            packets = _spammerPackets.ToArray();
                            sendToClient = _spammerSendToClient;
                            delay = _spammerDelay;
                        }
                        for (int index = 0; index < packets.Length && !token.IsCancellationRequested; index++)
                        {
                            lock (_spammerLock) _spammerSelectedIndex = index;
                            SendPacket(packets[index], sendToClient);
                            await Task.Delay(delay, token);
                        }
                        if (packets.Length == 0) await Task.Delay(Math.Max(50, delay), token);
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { }
                finally { lock (_spammerLock) _spammerSelectedIndex = -1; }
            }, token);
        }
        return PacketSpammerStatus();
    }

    private async Task<BridgeCommandResult> PacketSpammerStopAsync()
    {
        CancellationTokenSource? cts;
        Task? task;
        lock (_spammerLock) { cts = _spammerCts; task = _spammerTask; _spammerCts = null; _spammerTask = null; }
        cts?.Cancel();
        if (task is not null) try { await task; } catch (OperationCanceledException) { }
        cts?.Dispose();
        return PacketSpammerStatus();
    }

    private void SendPacket(string packet, bool sendToClient)
    {
        bool json = packet.StartsWith('{');
        if (sendToClient) _send.ClientPacket(packet, json ? "json" : "str");
        else _send.Packet(packet, json ? "Json" : "String");
    }

    private BridgeCommandResult PacketLoggerStatus()
    {
        lock (_packetLoggerLock)
        {
            return Ok(new
            {
                enabled = _packetLoggerEnabled,
                filters = _packetLoggerFilters.Select(x => new { name = x.Key, isChecked = x.Value }).ToArray(),
                logs = _packetLogs.ToArray()
            });
        }
    }

    private BridgeCommandResult PacketLoggerEnable(BridgeCommand command)
    {
        lock (_packetLoggerLock) _packetLoggerEnabled = ArgBool(command, 0, false);
        return PacketLoggerStatus();
    }

    private BridgeCommandResult PacketLoggerFilters(BridgeCommand command)
    {
        string? name = ArgString(command, 0);
        bool value = ArgBool(command, 1, true);
        lock (_packetLoggerLock)
        {
            if (string.Equals(name, "__clear__", StringComparison.Ordinal))
            {
                foreach (string key in _packetLoggerFilters.Keys.ToArray()) _packetLoggerFilters[key] = false;
            }
            else if (name is not null && _packetLoggerFilters.ContainsKey(name)) _packetLoggerFilters[name] = value;
        }
        return PacketLoggerStatus();
    }

    private BridgeCommandResult PacketLoggerClear()
    {
        lock (_packetLoggerLock) _packetLogs.Clear();
        return PacketLoggerStatus();
    }

    private void OnFlashCall(string function, object[] args)
    {
        if (function != "packet" || args.Length == 0) return;
        string packetText = args[0]?.ToString() ?? string.Empty;
        lock (_packetLoggerLock)
        {
            if (!_packetLoggerEnabled) return;
            bool filterEnabled = _packetLoggerFilters.Values.Any(isChecked => !isChecked);
            if (filterEnabled)
            {
                string[] packet = packetText.Split('%', StringSplitOptions.RemoveEmptyEntries);
                foreach (KeyValuePair<string, bool> filter in _packetLoggerFilters)
                    if (!filter.Value && PacketMatchesFilter(filter.Key, packet)) return;
            }
            _packetLogs.Add(packetText);
        }
    }

    private static bool PacketMatchesFilter(string name, string[] p)
    {
        string P(int i) => i >= 0 && i < p.Length ? p[i] : string.Empty;
        return name switch
        {
            "Combat" => p.Length >= 3 && (P(2) == "restRequest" || P(2) == "gar" || P(2) == "aggroMon"),
            "User Data" => p.Length >= 3 && (P(2) == "retrieveUserData" || P(2) == "retrieveUserDatas"),
            "Join" => p.Length >= 5 && (P(4) == "tfer" || P(2) == "house"),
            "Jump" => p.Length >= 3 && P(2) == "moveToCell",
            "Movement" => (p.Length >= 3 && P(2) == "mv") || P(2) == "mtcid",
            "Get Map" => p.Length >= 3 && P(2) == "getMapItem",
            "Quest" => p.Length >= 3 && (P(2) == "getQuest" || P(2) == "acceptQuest" || P(2) == "tryQuestComplete" || P(2) == "updateQuest"),
            "Shop" => p.Length >= 3 && (P(2) == "loadShop" || P(2) == "buyItem" || P(2) == "sellItem"),
            "Equip" => p.Length >= 3 && P(2) == "equipItem",
            "Drop" => p.Length >= 3 && P(2) == "getDrop",
            "Chat" => p.Length >= 3 && (P(2) == "message" || P(2) == "cc"),
            "Misc" => p.Length >= 3 && (P(2) == "crafting" || P(2) == "setHomeTown" || P(2) == "afk" || P(2) == "summonPet"),
            _ => false
        };
    }

    // ---------------------------------------------------------------------
    // Packet interceptor server selection
    // ---------------------------------------------------------------------

    private async Task<BridgeCommandResult> PacketInterceptorServersAsync()
    {
        List<Skua.Core.Models.Servers.Server> servers =
            await _servers.GetServers(false);

        if (servers.Count == 0)
            servers = _servers.CachedServers;

        return Ok(new
        {
            selectedServer = _servers.LastName,
            servers = servers.Select(server => new
            {
                name = server.Name,
                ip = server.IP,
                port = server.Port,
                online = server.Online,
                upgrade = server.Upgrade,
                playerCount = server.PlayerCount,
                maxPlayers = server.MaxPlayers
            }).ToArray()
        });
    }

    private async Task<BridgeCommandResult> PacketInterceptorReloginAsync(
        BridgeCommand command,
        CancellationToken cancellationToken)
    {
        string? serverName = ArgString(command, 0);
        if (string.IsNullOrWhiteSpace(serverName))
            return Fail("server-name-required");

        List<Skua.Core.Models.Servers.Server> servers =
            await _servers.GetServers(false);

        Skua.Core.Models.Servers.Server? server =
            servers.FirstOrDefault(candidate =>
                string.Equals(
                    candidate.Name,
                    serverName,
                    StringComparison.OrdinalIgnoreCase));

        if (server is null)
            return Fail("server-not-found");

        bool connected = await Task.Run(
            () => _servers.Relogin(server),
            cancellationToken);

        return Ok(new
        {
            connected,
            selectedServer = server.Name,
            server = new
            {
                name = server.Name,
                ip = server.IP,
                port = server.Port
            }
        });
    }

    // ---------------------------------------------------------------------
    // Plugins
    // ---------------------------------------------------------------------

    private BridgeCommandResult PluginsStatus() => Ok(new
    {
        pluginsDirectory = ClientFileSources.SkuaPluginsDIR,
        plugins = _plugins.Containers.Select(container => new
        {
            name = container.Plugin.Name,
            author = container.Plugin.Author,
            description = container.Plugin.Description,
            optionsStorage = container.Plugin.OptionsStorage,
            hasOptions = container.OptionContainer.Options.Count > 0 || container.OptionContainer.MultipleOptions.Count > 0
        }).ToArray()
    });

    private BridgeCommandResult PluginsLoad(BridgeCommand command)
    {
        string? path = ArgString(command, 0);
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return Fail("plugin-file-not-found");
        Exception? error = _plugins.Load(Path.GetFullPath(path));
        if (error is not null) return Fail(error.ToString());
        return PluginsStatus();
    }

    private BridgeCommandResult PluginsUnload(BridgeCommand command)
    {
        string? name = ArgString(command, 0);
        if (string.IsNullOrWhiteSpace(name)) return Fail("plugin-name-required");
        _plugins.Unload(name);
        return PluginsStatus();
    }

    private BridgeCommandResult PluginsUnloadAll()
    {
        foreach (IPluginContainer container in _plugins.Containers.ToArray()) _plugins.Unload(container.Plugin);
        return PluginsStatus();
    }

    private BridgeCommandResult PluginOptionsStatus(BridgeCommand command)
    {
        string? name = ArgString(command, 0);
        IPluginContainer? container = name is null ? null : _plugins.GetContainer(name);
        if (container is null) return Fail("plugin-not-found");
        container.OptionContainer.Load();
        List<object> options = new();
        foreach (IOption option in container.OptionContainer.Options) options.Add(DescribeOption(container.OptionContainer, "Options", option));
        foreach (KeyValuePair<string, List<IOption>> group in container.OptionContainer.MultipleOptions)
            foreach (IOption option in group.Value) options.Add(DescribeOption(container.OptionContainer, group.Key, option));
        return Ok(new { plugin = name, options });
    }

    private BridgeCommandResult PluginOptionsSet(BridgeCommand command)
    {
        string? plugin = ArgString(command, 0);
        string category = ArgString(command, 1) ?? "Options";
        string? name = ArgString(command, 2);
        string value = ArgString(command, 3) ?? string.Empty;
        IPluginContainer? container = plugin is null ? null : _plugins.GetContainer(plugin);
        if (container is null || string.IsNullOrWhiteSpace(name)) return Fail("plugin-or-option-not-found");
        if (category == "Options") container.OptionContainer.Set(name, value); else container.OptionContainer.Set(category, name, value);
        container.OptionContainer.Save();
        return PluginOptionsStatus(new BridgeCommand(command.Id, command.Name, new[] { ToJsonElement(plugin) }));
    }

    // ---------------------------------------------------------------------
    // Hot keys (same HotKeys StringCollection storage used by WPF)
    // ---------------------------------------------------------------------

    private static readonly string[] HotKeyBindings =
    {
        "ToggleScript", "LoadScript", "OpenBank", "OpenConsole", "ToggleAutoAttack", "ToggleAutoHunt", "ToggleLagKiller"
    };

    private BridgeCommandResult HotKeysStatus()
    {
        StringCollection? stored = _settings.Get<StringCollection>("HotKeys");
        Dictionary<string, string> values = new(StringComparer.Ordinal);
        if (stored is not null)
        {
            foreach (string? line in stored)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                string[] parts = line.Split('|', 2);
                if (parts.Length == 2) values[parts[0]] = parts[1];
            }
        }
        if (!values.ContainsKey("ToggleLagKiller") && !values.Values.Any(x => x.Equals("F6", StringComparison.OrdinalIgnoreCase))) values["ToggleLagKiller"] = "F6";
        return Ok(new { bindings = HotKeyBindings.Select(name => new { name, gesture = values.TryGetValue(name, out string? gesture) ? gesture : string.Empty }).ToArray() });
    }

    private BridgeCommandResult HotKeysSave(BridgeCommand command)
    {
        if (command.Arguments.Count == 0 || command.Arguments[0].ValueKind != JsonValueKind.Object) return Fail("hotkey-map-required");
        StringCollection lines = new();
        foreach (JsonProperty property in command.Arguments[0].EnumerateObject())
        {
            if (!HotKeyBindings.Contains(property.Name, StringComparer.Ordinal)) continue;
            string gesture = property.Value.GetString() ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(gesture)) lines.Add($"{property.Name}|{gesture}");
        }
        _settings.Set("HotKeys", lines);
        return HotKeysStatus();
    }

    // ---------------------------------------------------------------------
    // CoreBots storage. Exact tags/defaults come from CoreBots.CreateOptions.
    // ---------------------------------------------------------------------

    private static readonly Dictionary<string, object> CoreBotsDefaults = new(StringComparer.Ordinal)
    {
        ["PrivateRooms"] = true, ["PublicDifficult"] = false, ["BankMiscAC"] = true, ["LoggerInChat"] = true,
        ["MessageBoxCheck"] = false, ["RestCheck"] = false, ["DisableAutoEnhance"] = false, ["DisableBestGear"] = false,
        ["AntiLag"] = true, ["IncognitoMode"] = true, ["PrivateRoomNr"] = 100000, ["ActionDelayNr"] = 700,
        ["ExitCombatNr"] = 1600, ["HuntDelayNr"] = 100, ["QuestTriesNr"] = 20, ["QuestMaxNr"] = 150,
        ["StopLocationSelect"] = "Home", ["doGoldBoost"] = false, ["doClassBoost"] = false, ["doRepBoost"] = false,
        ["doExpBoost"] = false, ["Nation_SellMemVoucher"] = true, ["Nation_ReturnPolicyDuringSupplies"] = true,
        ["UltraAlteonForSupplies"] = false, ["PvP_SoloPvPBoss"] = false, ["BCO_Story_TestBot"] = false,
        ["SoloClassSelect"] = "", ["SoloEquipCheck"] = false, ["SoloModeSelect"] = "Base",
        ["FarmClassSelect"] = "", ["FarmEquipCheck"] = false, ["FarmModeSelect"] = "Base",
        ["DodgeClassSelect"] = "", ["DodgeEquipCheck"] = false, ["DodgeModeSelect"] = "Base",
        ["BossClassSelect"] = "", ["BossEquipCheck"] = false, ["BossModeSelect"] = "Base",
        ["Helm1Select"] = "", ["Armor1Select"] = "", ["Cape1Select"] = "", ["Weapon1Select"] = "", ["Pet1Select"] = "", ["GroundItem1Select"] = "",
        ["Helm2Select"] = "", ["Armor2Select"] = "", ["Cape2Select"] = "", ["Weapon2Select"] = "", ["Pet2Select"] = "", ["GroundItem2Select"] = "",
        ["Helm3Select"] = "", ["Armor3Select"] = "", ["Cape3Select"] = "", ["Weapon3Select"] = "", ["Pet3Select"] = "", ["GroundItem3Select"] = "",
        ["Helm4Select"] = "", ["Armor4Select"] = "", ["Cape4Select"] = "", ["Weapon4Select"] = "", ["Pet4Select"] = "", ["GroundItem4Select"] = ""
    };

    private string CoreBotsFile => Path.Combine(ClientFileSources.SkuaOptionsDIR, $"CBO_Storage({_player.Username}).txt");

    private Dictionary<string, string> ReadCoreBotsValues()
    {
        Dictionary<string, string> values = CoreBotsDefaults.ToDictionary(x => x.Key, x => x.Value?.ToString() ?? string.Empty, StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(_player.Username) && File.Exists(CoreBotsFile))
        {
            foreach (string line in File.ReadLines(CoreBotsFile))
            {
                string[] parts = line.Split(':', 2, StringSplitOptions.TrimEntries);
                if (parts.Length == 2) values[parts[0]] = parts[1];
            }
        }
        return values;
    }

    private BridgeCommandResult CoreBotsStatus()
    {
        if (string.IsNullOrWhiteSpace(_player.Username)) return Fail("login-required");
        Dictionary<string, string> values = ReadCoreBotsValues();
        List<string> classes = new() { "[Current]" };
        if (_inventory.Items is not null)
            classes.AddRange(_inventory.Items.Where(item => item.Category.ToString() == "Class" && GetIntProperty(item, "EnhancementLevel") > 0).Select(item => item.Name).OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
        object equipment = new
        {
            helms = InventoryNames(item => GetProperty(item, "Category")?.ToString() == "Helm" && GetIntProperty(item, "EnhancementLevel") > 0),
            armors = InventoryNames(item => GetProperty(item, "Category")?.ToString() == "Armor"),
            capes = InventoryNames(item => GetProperty(item, "Category")?.ToString() == "Cape" && GetIntProperty(item, "EnhancementLevel") > 0),
            weapons = InventoryNames(item => string.Equals(GetProperty(item, "ItemGroup")?.ToString(), "Weapon", StringComparison.Ordinal) && GetIntProperty(item, "EnhancementLevel") > 0),
            pets = InventoryNames(item => GetProperty(item, "Category")?.ToString() == "Pet"),
            groundItems = InventoryNames(item => GetProperty(item, "Category")?.ToString() == "Misc")
        };
        return Ok(new
        {
            username = _player.Username,
            file = CoreBotsFile,
            values,
            defaults = CoreBotsDefaults,
            classes,
            availableModes = _advancedSkills.GetAvailableClassModes(),
            equipment
        });
    }

    private BridgeCommandResult CoreBotsSave(BridgeCommand command)
    {
        if (string.IsNullOrWhiteSpace(_player.Username)) return Fail("login-required");
        if (command.Arguments.Count == 0 || command.Arguments[0].ValueKind != JsonValueKind.Object) return Fail("corebots-values-required");
        Dictionary<string, string> values = ReadCoreBotsValues();
        foreach (JsonProperty property in command.Arguments[0].EnumerateObject()) values[property.Name] = property.Value.ToString();
        if (long.TryParse(values.GetValueOrDefault("PrivateRoomNr"), out long room) && room > int.MaxValue) values["PrivateRoomNr"] = "100000";
        Directory.CreateDirectory(ClientFileSources.SkuaOptionsDIR);
        File.WriteAllLines(CoreBotsFile, values.Select(x => $"{x.Key}: {x.Value}"));
        return CoreBotsStatus();
    }

    private string[] InventoryNames(Func<object, bool> predicate)
    {
        if (_inventory.Items is null) return Array.Empty<string>();
        List<string> names = new();
        foreach (object item in _inventory.Items)
        {
            try
            {
                if (predicate(item))
                {
                    string? name = GetProperty(item, "Name")?.ToString();
                    if (!string.IsNullOrWhiteSpace(name)) names.Add(name);
                }
            }
            catch { }
        }
        names.Sort(StringComparer.OrdinalIgnoreCase);
        return names.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    // ---------------------------------------------------------------------
    // Junk items
    // ---------------------------------------------------------------------

    private IJunkService Junk => _services.GetRequiredService<IJunkService>();

    private BridgeCommandResult JunkStatus()
    {
        Junk.Load();
        try { if (!_bank.Loaded) _bank.Load(); } catch { }
        IEnumerable<object> combined = Enumerable.Empty<object>();
        if (_inventory.Items is not null) combined = combined.Concat(_inventory.Items.Cast<object>());
        if (_bank.Items is not null) combined = combined.Concat(_bank.Items.Cast<object>());
        List<object> items = combined.GroupBy(item => GetIntProperty(item, "ID")).Select(group => group.First()).OrderBy(item => GetProperty(item, "Name")?.ToString()).Select(item => new
        {
            id = GetIntProperty(item, "ID"),
            name = GetProperty(item, "Name")?.ToString() ?? string.Empty,
            junk = Junk.IsJunk(GetIntProperty(item, "ID")),
            inBank = _bank.Items?.Any(bankItem => bankItem.ID == GetIntProperty(item, "ID")) ?? false
        } as object).ToList();
        return Ok(new { items, skipSellWarning = _settings.Get("JunkSkipSellWarning", false) });
    }

    private BridgeCommandResult JunkSet(BridgeCommand command)
    {
        int id = ArgInt(command, 0, -1);
        bool junk = ArgBool(command, 1, true);
        if (id < 0) return Fail("item-id-required");
        Junk.Load();
        List<Skua.Core.Models.Items.JunkItemConfig> configs = Junk.JunkItems.ToList();
        configs.RemoveAll(x => x.ID == id);
        if (junk)
        {
            object? item = _inventory.Items?.Cast<object>().Concat(_bank.Items?.Cast<object>() ?? Enumerable.Empty<object>()).FirstOrDefault(x => GetIntProperty(x, "ID") == id);
            string name = GetProperty(item, "Name")?.ToString() ?? id.ToString(CultureInfo.InvariantCulture);
            configs.Add(new Skua.Core.Models.Items.JunkItemConfig { ID = id, Name = name });
        }
        Junk.SetJunk(configs);
        Junk.Save();
        return JunkStatus();
    }

    private BridgeCommandResult JunkClear()
    {
        Junk.SetJunk(Array.Empty<Skua.Core.Models.Items.JunkItemConfig>());
        Junk.Save();
        return JunkStatus();
    }

    private BridgeCommandResult JunkSellAll()
    {
        Junk.SellAllJunk();
        return Ok(new { started = true });
    }

    private BridgeCommandResult JunkWarning(BridgeCommand command)
    {
        _settings.Set("JunkSkipSellWarning", ArgBool(command, 0, false));
        return JunkStatus();
    }

    // ---------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------

    private static BridgeCommandResult Ok(object? result = null) => BridgeCommandResult.Ok(result);
    private static BridgeCommandResult Fail(string error) => BridgeCommandResult.Failure(error);

    private static BridgeCommandResult Run(Action action)
    {
        action();
        return Ok(new { success = true });
    }

    private static string? ArgString(BridgeCommand command, int index)
    {
        if (index < 0 || index >= command.Arguments.Count) return null;
        JsonElement value = command.Arguments[index];
        return value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
    }

    private static int ArgInt(BridgeCommand command, int index, int fallback)
    {
        if (index < 0 || index >= command.Arguments.Count) return fallback;
        JsonElement value = command.Arguments[index];
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number)) return number;
        return int.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) ? parsed : fallback;
    }

    private static double ArgDouble(BridgeCommand command, int index, double fallback)
    {
        if (index < 0 || index >= command.Arguments.Count) return fallback;
        JsonElement value = command.Arguments[index];
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double number)) return number;
        return double.TryParse(value.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out double parsed)
            ? parsed
            : fallback;
    }

    private static float ArgFloat(BridgeCommand command, int index, float fallback)
    {
        if (index < 0 || index >= command.Arguments.Count) return fallback;
        JsonElement value = command.Arguments[index];
        if (value.ValueKind == JsonValueKind.Number && value.TryGetSingle(out float number)) return number;
        return float.TryParse(value.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed) ? parsed : fallback;
    }

    private static bool ArgBool(BridgeCommand command, int index, bool fallback)
    {
        if (index < 0 || index >= command.Arguments.Count) return fallback;
        JsonElement value = command.Arguments[index];
        if (value.ValueKind == JsonValueKind.True) return true;
        if (value.ValueKind == JsonValueKind.False) return false;
        return bool.TryParse(value.ToString(), out bool parsed) ? parsed : fallback;
    }

    private static JsonElement[] ArgArray(BridgeCommand command, int index)
    {
        if (index < 0 || index >= command.Arguments.Count || command.Arguments[index].ValueKind != JsonValueKind.Array) return Array.Empty<JsonElement>();
        return command.Arguments[index].EnumerateArray().Select(x => x.Clone()).ToArray();
    }

    private static object? ConvertJsonArg(BridgeCommand command, int index, Type type)
    {
        if (index < 0 || index >= command.Arguments.Count) return type.IsValueType ? Activator.CreateInstance(type) : null;
        JsonElement value = command.Arguments[index];
        if (type == typeof(string)) return value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
        if (type == typeof(bool)) return value.ValueKind == JsonValueKind.True || (value.ValueKind != JsonValueKind.False && bool.TryParse(value.ToString(), out bool b) && b);
        if (type == typeof(int)) return int.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int i) ? i : 0;
        if (type.IsEnum) return Enum.Parse(type, value.ToString().Replace(' ', '_'), true);
        return JsonSerializer.Deserialize(value.GetRawText(), type);
    }

    private static int[]? ParseIds(string? input, char[] separators)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        List<int> ids = new();
        foreach (string part in input.Split(separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (int.TryParse(part, out int id)) ids.Add(id);
        return ids.Count == 0 ? null : ids.ToArray();
    }

    private static string Decamelize(string value)
    {
        if (string.IsNullOrEmpty(value)) return value;
        System.Text.StringBuilder builder = new();
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (i > 0 && char.IsUpper(c) && !char.IsUpper(value[i - 1])) builder.Append(' ');
            builder.Append(c);
        }
        return builder.ToString();
    }

    private static object DescribeItem(object item)
    {
        return new
        {
            id = GetIntProperty(item, "ID"),
            name = GetProperty(item, "Name")?.ToString() ?? string.Empty,
            quantity = GetIntProperty(item, "Quantity"),
            category = GetProperty(item, "Category")?.ToString() ?? string.Empty,
            coins = GetBoolProperty(item, "Coins"),
            equipped = GetBoolProperty(item, "Equipped")
        };
    }

    private static object? GetProperty(object? value, string property)
        => value?.GetType().GetProperty(property, BindingFlags.Instance | BindingFlags.Public)?.GetValue(value);

    private static int GetIntProperty(object? value, string property)
        => GetProperty(value, property) is object raw && int.TryParse(raw.ToString(), out int result) ? result : 0;

    private static bool GetBoolProperty(object? value, string property)
        => GetProperty(value, property) is object raw && bool.TryParse(raw.ToString(), out bool result) && result;

    private static JsonElement ToJsonElement(object? value)
    {
        using JsonDocument document = JsonDocument.Parse(JsonSerializer.Serialize(value));
        return document.RootElement.Clone();
    }

    public async ValueTask DisposeAsync()
    {
        _flash.FlashCall -= OnFlashCall;
        StrongReferenceMessenger.Default.UnregisterAll(this);
        lock (_loaderLock) _loaderCts?.Cancel();
        if (_loaderTask is not null)
        {
            try { await _loaderTask; } catch { }
        }
        _loaderCts?.Dispose();
        await PacketSpammerStopAsync();
    }
}
