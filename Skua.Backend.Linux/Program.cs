using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Skua.Backend.Linux.Bridge;
using Skua.Backend.Linux.Flash;
using Skua.Backend.Linux.Services;
using Skua.Core.AppStartup;
using Skua.Core.Interfaces;
using Skua.Core.Models;
using Skua.Core.Models.GitHub;
using System.Text.Json;

Console.WriteLine(
    "Skua Backend Linux"
);

Console.WriteLine(
    "Modo serviço permanente"
);

using CancellationTokenSource shutdown =
new();

Console.CancelKeyPress += (
    _,
    eventArgs
) =>
{
    eventArgs.Cancel = true;
    shutdown.Cancel();
};

TaskCompletionSource<bool> gameLoaded =
new(
    TaskCreationOptions
    .RunContinuationsAsynchronously
);

IScriptManager? scriptManager = null;
IScriptInterfaceManager? scriptInterfaceManager = null;
IScriptAuto? scriptAuto = null;
IScriptCombat? scriptCombat = null;
IScriptPlayer? scriptPlayer = null;
IScriptBank? scriptBank = null;
IMapService? mapService = null;
IGetScriptsService? scriptRepository = null;

bool backendReady = false;
string backendPhase = "starting";

bool scriptPaused = false;
string pausedScriptPath = string.Empty;

object repositoryStateLock = new();
SemaphoreSlim repositoryDownloadGate = new(1, 1);
bool repositorySyncRunning = false;
string repositorySyncMessage = "Repository not loaded yet.";
int repositoryLastDownloadCount = 0;
bool repositoryLastSyncAutomatic = false;
long repositorySyncSequence = 0;
CancellationTokenSource? repositoryUiOperationCts = null;
Task? repositoryUiOperationTask = null;

string FindProjectRoot()
{
    string? configuredRoot =
        Environment.GetEnvironmentVariable(
            "SKUA_PROJECT_ROOT"
        );

    if (!string.IsNullOrWhiteSpace(configuredRoot))
    {
        return Path.GetFullPath(configuredRoot);
    }

    IEnumerable<string> candidates =
        new[]
        {
            Directory.GetCurrentDirectory(),
            AppContext.BaseDirectory
        };

    foreach (string candidate in candidates)
    {
        DirectoryInfo? current =
            new(Path.GetFullPath(candidate));

        while (current is not null)
        {
            if (
                Directory.Exists(
                    Path.Combine(
                        current.FullName,
                        "Skua.Backend.Linux"
                    )
                )
            )
            {
                return current.FullName;
            }

            current = current.Parent;
        }
    }

    return Directory.GetCurrentDirectory();
}

string projectRoot = FindProjectRoot();
string projectScriptsDirectory =
    Path.Combine(projectRoot, "scripts");

Directory.CreateDirectory(projectScriptsDirectory);

string? GetCommandString(
    BridgeCommand command,
    int index
)
{
    if (
        index < 0 ||
        index >= command.Arguments.Count
    )
    {
        return null;
    }

    JsonElement value = command.Arguments[index];

    return value.ValueKind == JsonValueKind.String
        ? value.GetString()
        : value.ToString();
}

int GetCommandInt(
    BridgeCommand command,
    int index,
    int fallback
)
{
    if (
        index < 0 ||
        index >= command.Arguments.Count
    )
    {
        return fallback;
    }

    JsonElement value = command.Arguments[index];

    if (
        value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt32(out int number)
    )
    {
        return number;
    }

    return int.TryParse(
        value.ToString(),
        out int parsed
    )
        ? parsed
        : fallback;
}

const string LinuxCoreBotsCompatMarkerPrefix =
    "// SKUA_LINUX_COREBOTS_COMPAT_SOURCE: ";

const string LinuxCoreBotsCompatRevisionMarker =
    "// SKUA_LINUX_COREBOTS_COMPAT_REVISION: 2";

bool IsCoreBotsScript(ScriptInfo info)
{
    return string.Equals(
        Path.GetFileName(info.FilePath),
        "CoreBots.cs",
        StringComparison.OrdinalIgnoreCase
    );
}

string GetCoreBotsSourceToken(ScriptInfo info)
{
    return !string.IsNullOrWhiteSpace(info.Sha256)
        ? info.Sha256!
        : $"size-{info.Size}";
}

string? ReadCoreBotsCompatToken(string path)
{
    if (!File.Exists(path))
    {
        return null;
    }

    try
    {
        using StreamReader reader = new(path);
        string? firstLine = reader.ReadLine();

        if (
            firstLine is null ||
            !firstLine.StartsWith(
                LinuxCoreBotsCompatMarkerPrefix,
                StringComparison.Ordinal
            )
        )
        {
            return null;
        }

        return firstLine[
            LinuxCoreBotsCompatMarkerPrefix.Length..
        ].Trim();
    }
    catch
    {
        return null;
    }
}

bool HasCurrentCoreBotsCompatRevision(string path)
{
    if (!File.Exists(path))
    {
        return false;
    }

    try
    {
        using StreamReader reader = new(path);

        for (int index = 0; index < 4; index++)
        {
            string? line = reader.ReadLine();

            if (line is null)
            {
                break;
            }

            if (string.Equals(
                line.Trim(),
                LinuxCoreBotsCompatRevisionMarker,
                StringComparison.Ordinal
            ))
            {
                return true;
            }
        }
    }
    catch
    {
        return false;
    }

    return false;
}

bool IsLinuxCompatibleCoreBots(ScriptInfo info)
{
    if (!IsCoreBotsScript(info) || !info.Downloaded)
    {
        return false;
    }

    string? markerToken =
        ReadCoreBotsCompatToken(info.LocalFile);

    return
        string.Equals(
            markerToken,
            GetCoreBotsSourceToken(info),
            StringComparison.OrdinalIgnoreCase
        ) &&
        HasCurrentCoreBotsCompatRevision(
            info.LocalFile
        );
}

bool IsScriptOutdatedForLinux(ScriptInfo info)
{
    return info.Outdated &&
        !IsLinuxCompatibleCoreBots(info);
}

async Task<bool> ApplyLinuxCoreBotsCompatibilityAsync(
    ScriptInfo info,
    CancellationToken cancellationToken
)
{
    if (!IsCoreBotsScript(info) || !info.Downloaded)
    {
        return false;
    }

    string sourceToken =
        GetCoreBotsSourceToken(info);

    string? existingToken =
        ReadCoreBotsCompatToken(info.LocalFile);

    bool sameSourceToken =
        string.Equals(
            existingToken,
            sourceToken,
            StringComparison.OrdinalIgnoreCase
        );

    if (
        sameSourceToken &&
        HasCurrentCoreBotsCompatRevision(
            info.LocalFile
        )
    )
    {
        return true;
    }

    /*
     * Se o arquivo já tem um marker de outra versão,
     * o manifesto mudou. Não devemos "carimbar" a cópia
     * antiga como atual; o sincronizador precisa baixá-la
     * novamente primeiro.
     *
     * Um marker da revisão anterior COM o mesmo token é
     * aceito aqui para que possamos aplicar novos ajustes
     * Linux sem exigir um download desnecessário.
     */
    if (
        !string.IsNullOrWhiteSpace(existingToken) &&
        !sameSourceToken
    )
    {
        return false;
    }

    bool matchesRepositorySource;

    if (!string.IsNullOrWhiteSpace(info.Sha256))
    {
        matchesRepositorySource =
            string.Equals(
                info.LocalSha256,
                info.Sha256,
                StringComparison.OrdinalIgnoreCase
            );
    }
    else
    {
        matchesRepositorySource =
            info.LocalSize == info.Size;
    }

    if (
        !matchesRepositorySource &&
        !sameSourceToken
    )
    {
        return false;
    }

    string source =
        await File.ReadAllTextAsync(
            info.LocalFile,
            cancellationToken
        );

    /*
     * Remove metadados da revisão anterior antes de
     * reconstruir o arquivo. Isso mantém o CoreBots
     * legível e evita markers duplicados a cada upgrade
     * da camada de compatibilidade Linux.
     */
    if (source.StartsWith(
        LinuxCoreBotsCompatMarkerPrefix,
        StringComparison.Ordinal
    ))
    {
        int firstLineEnd = source.IndexOf('\n');

        if (firstLineEnd >= 0)
        {
            source = source[(firstLineEnd + 1)..];
        }
    }

    if (source.StartsWith(
        LinuxCoreBotsCompatRevisionMarker,
        StringComparison.Ordinal
    ))
    {
        int revisionLineEnd = source.IndexOf('\n');

        if (revisionLineEnd >= 0)
        {
            source = source[(revisionLineEnd + 1)..];
        }
    }

    string patched = source
        .Replace(
            "using System.Drawing;\r\n",
            string.Empty,
            StringComparison.Ordinal
        )
        .Replace(
            "using System.Drawing;\n",
            string.Empty,
            StringComparison.Ordinal
        )
        .Replace(
            "using System.Windows.Forms;\r\n",
            string.Empty,
            StringComparison.Ordinal
        )
        .Replace(
            "using System.Windows.Forms;\n",
            string.Empty,
            StringComparison.Ordinal
        );

    /*
     * O updater embutido no CoreBots oficial é exclusivo
     * de Windows: ele exige Windows 10+ e posteriormente
     * baixa/abre um instalador MSI. No backend Linux nativo
     * essa verificação não se aplica. Mantemos o checker
     * original intacto para Windows e apenas retornamos cedo
     * quando o script está sendo executado em outro SO.
     */
    const string versionCheckerAnchor =
        "        if (Bot.Version == null || Bot.Version.ToString() == \"1.3.3.2\")\n" +
        "            return;\n\n";

    const string linuxVersionCheckerGuard =
        "        // Skua Linux compatibility: Windows MSI updater does not apply here.\n" +
        "        if (!OperatingSystem.IsWindows())\n" +
        "            return;\n\n";

    if (
        !patched.Contains(
            linuxVersionCheckerGuard,
            StringComparison.Ordinal
        )
    )
    {
        int checkerMethod = patched.IndexOf(
            "private async Task SkuaVersionCheckerAsync()",
            StringComparison.Ordinal
        );

        int checkerAnchor = checkerMethod >= 0
            ? patched.IndexOf(
                versionCheckerAnchor,
                checkerMethod,
                StringComparison.Ordinal
            )
            : -1;

        if (checkerAnchor < 0)
        {
            throw new InvalidOperationException(
                "CoreBots Windows version checker was not found " +
                "in the expected shape for Linux compatibility."
            );
        }

        int insertAt =
            checkerAnchor +
            versionCheckerAnchor.Length;

        patched =
            patched[..insertAt] +
            linuxVersionCheckerGuard +
            patched[insertAt..];
    }

    /*
     * O CoreBots oficial contém um bloco de April Fools
     * que constrói Form/ProgressBar/Application.Run.
     * Ele é puramente visual/Windows e não participa da
     * automação do AQW. Removemos apenas esse case.
     */
    int windowsOnlyUi = patched.IndexOf(
        "Deleting C:\\\\Windows\\\\System32",
        StringComparison.Ordinal
    );

    if (windowsOnlyUi >= 0)
    {
        int caseStart = patched.LastIndexOf(
            "case 6:",
            windowsOnlyUi,
            StringComparison.Ordinal
        );

        int nextCase = patched.IndexOf(
            "case 7:",
            windowsOnlyUi,
            StringComparison.Ordinal
        );

        if (
            caseStart < 0 ||
            nextCase <= caseStart
        )
        {
            throw new InvalidOperationException(
                "CoreBots Windows compatibility block was found, " +
                "but its case boundaries could not be identified."
            );
        }

        string replacement =
            "case 6:" + Environment.NewLine +
            "                          break;" +
            Environment.NewLine + Environment.NewLine +
            "                      ";

        patched =
            patched[..caseStart] +
            replacement +
            patched[nextCase..];
    }

    if (
        patched.Contains(
            "using System.Windows.Forms;",
            StringComparison.Ordinal
        ) ||
        patched.Contains(
            "System.Windows.Forms.MethodInvoker",
            StringComparison.Ordinal
        ) ||
        patched.Contains(
            "Application.Run(progressForm)",
            StringComparison.Ordinal
        )
    )
    {
        throw new InvalidOperationException(
            "CoreBots still contains WinForms-only code after " +
            "the Linux compatibility pass."
        );
    }

    if (
        !patched.Contains(
            linuxVersionCheckerGuard,
            StringComparison.Ordinal
        )
    )
    {
        throw new InvalidOperationException(
            "CoreBots still contains an active Windows-only " +
            "version gate after the Linux compatibility pass."
        );
    }

    patched =
        LinuxCoreBotsCompatMarkerPrefix +
        sourceToken +
        Environment.NewLine +
        LinuxCoreBotsCompatRevisionMarker +
        Environment.NewLine +
        patched;

    await File.WriteAllTextAsync(
        info.LocalFile,
        patched,
        cancellationToken
    );

    Console.WriteLine(
        "[Scripts] CoreBots Linux compatibility applied " +
        $"for source {sourceToken}."
    );

    return true;
}

async Task ApplyLinuxScriptCompatibilityAsync(
    CancellationToken cancellationToken
)
{
    if (scriptRepository is null)
    {
        return;
    }

    foreach (ScriptInfo info in scriptRepository.Scripts)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!IsCoreBotsScript(info))
        {
            continue;
        }

        await ApplyLinuxCoreBotsCompatibilityAsync(
            info,
            cancellationToken
        );
    }
}

void SetRepositoryState(
    bool running,
    string message,
    int? downloaded = null
)
{
    lock (repositoryStateLock)
    {
        repositorySyncRunning = running;
        repositorySyncMessage = message;

        if (downloaded.HasValue)
        {
            repositoryLastDownloadCount =
                downloaded.Value;
        }
    }

    Console.WriteLine(
        $"[Scripts] {message}"
    );
}

(
    bool Running,
    string Message,
    int Downloaded,
    bool LastSyncAutomatic,
    long SyncSequence
)
GetRepositoryState()
{
    lock (repositoryStateLock)
    {
        return (
            repositorySyncRunning,
            repositorySyncMessage,
            repositoryLastDownloadCount,
            repositoryLastSyncAutomatic,
            repositorySyncSequence
        );
    }
}

string GetProjectScriptPath(ScriptInfo info)
{
    string relative =
        info.FilePath
            .Replace('/', Path.DirectorySeparatorChar)
            .Replace('\\', Path.DirectorySeparatorChar)
            .TrimStart(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar
            );

    string root =
        Path.GetFullPath(projectScriptsDirectory) +
        Path.DirectorySeparatorChar;

    string fullPath =
        Path.GetFullPath(
            Path.Combine(
                projectScriptsDirectory,
                relative
            )
        );

    if (!fullPath.StartsWith(
        root,
        StringComparison.Ordinal
    ))
    {
        throw new InvalidOperationException(
            "Script repository path escaped the project scripts directory."
        );
    }

    return fullPath;
}

async Task MirrorScriptToProjectAsync(
    ScriptInfo info,
    CancellationToken cancellationToken
)
{
    if (!info.Downloaded)
    {
        return;
    }

    string destination =
        GetProjectScriptPath(info);

    string? directory =
        Path.GetDirectoryName(destination);

    if (!string.IsNullOrWhiteSpace(directory))
    {
        Directory.CreateDirectory(directory);
    }

    await using FileStream source =
        File.Open(
            info.LocalFile,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite
        );

    await using FileStream target =
        File.Open(
            destination,
            FileMode.Create,
            FileAccess.Write,
            FileShare.Read
        );

    await source.CopyToAsync(
        target,
        cancellationToken
    );
}

async Task MirrorDownloadedScriptsToProjectAsync(
    CancellationToken cancellationToken
)
{
    if (scriptRepository is null)
    {
        return;
    }

    foreach (ScriptInfo info in scriptRepository.Scripts)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!info.Downloaded)
        {
            continue;
        }

        await MirrorScriptToProjectAsync(
            info,
            cancellationToken
        );
    }
}

object GetRepositoryStats()
{
    if (scriptRepository is null)
    {
        return new
        {
            total = 0,
            visible = 0,
            downloaded = 0,
            missing = 0,
            outdated = 0,
            syncRunning = false,
            syncMessage = "backend-not-ready",
            scriptsDirectory = projectScriptsDirectory
        };
    }

    int total = scriptRepository.Scripts.Count;
    int visible =
        scriptRepository.Scripts.Count(
            script =>
                script is not null &&
                script.Name is not null &&
                !script.Name.Equals("null")
        );

    int downloaded =
        scriptRepository.Scripts.Count(
            script => script.Downloaded
        );

    /*
     * Para a UI usamos tamanho como indicador rápido de
     * desatualização. O serviço original continua usando
     * ScriptInfo.Outdated (incluindo SHA-256) ao atualizar.
     */
    int outdated =
        scriptRepository.Scripts.Count(
            script =>
                script.Downloaded &&
                IsScriptOutdatedForLinux(script)
        );

    var state = GetRepositoryState();

    return new
    {
        total,
        visible,
        downloaded,
        missing = total - downloaded,
        outdated,
        syncRunning = state.Running,
        syncMessage = state.Message,
        lastDownloadCount = state.Downloaded,
        lastSyncAutomatic = state.LastSyncAutomatic,
        syncSequence = state.SyncSequence,
        scriptsDirectory = projectScriptsDirectory
    };
}

async Task<(bool Success, int Count, string Message)>
SynchronizeRepositoryAsync(
    bool onlyOutdated,
    CancellationToken cancellationToken,
    bool automatic = false
)
{
    if (scriptRepository is null)
    {
        return (false, 0, "backend-not-ready");
    }

    if (!await repositoryDownloadGate.WaitAsync(
        0,
        cancellationToken
    ))
    {
        var busyState = GetRepositoryState();
        return (
            false,
            0,
            string.IsNullOrWhiteSpace(busyState.Message)
                ? "repository-sync-running"
                : busyState.Message
        );
    }

    try
    {
        SetRepositoryState(
            true,
            onlyOutdated
                ? "Updating outdated scripts..."
                : "Downloading missing/outdated scripts..."
        );

        await MirrorDownloadedScriptsToProjectAsync(
            cancellationToken
        );

        Func<ScriptInfo, bool> predicate =
            onlyOutdated
                ? script =>
                    IsScriptOutdatedForLinux(script)
                : script =>
                    !script.Downloaded ||
                    IsScriptOutdatedForLinux(script);

        int count =
            await scriptRepository.DownloadAllWhereAsync(
                predicate
            );

        await ApplyLinuxScriptCompatibilityAsync(
            cancellationToken
        );

        await MirrorDownloadedScriptsToProjectAsync(
            cancellationToken
        );

        string message =
            $"Script sync complete. {count} file(s) downloaded; " +
            $"project mirror: {projectScriptsDirectory}";

        SetRepositoryState(
            false,
            message,
            count
        );

        lock (repositoryStateLock)
        {
            repositoryLastSyncAutomatic =
                automatic;
            repositorySyncSequence++;
        }

        return (true, count, message);
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
        const string message = "Script sync canceled.";
        SetRepositoryState(false, message);
        return (false, 0, message);
    }
    catch (Exception exception)
    {
        string message =
            $"Script sync failed: {exception.Message}";

        SetRepositoryState(
            false,
            message
        );

        return (false, 0, message);
    }
    finally
    {
        repositoryDownloadGate.Release();
    }
}


BridgeCommandResult StartRepositoryUiSync(bool onlyOutdated)
{
    if (scriptRepository is null)
    {
        return BridgeCommandResult.Failure("backend-not-ready");
    }

    if (GetRepositoryState().Running || repositoryUiOperationTask is { IsCompleted: false })
    {
        return BridgeCommandResult.Failure("repository-sync-running");
    }

    repositoryUiOperationCts?.Dispose();
    repositoryUiOperationCts =
        CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token);

    CancellationToken token = repositoryUiOperationCts.Token;

    SetRepositoryState(
        true,
        onlyOutdated
            ? "Updating outdated scripts..."
            : "Downloading missing/outdated scripts...");

    repositoryUiOperationTask = Task.Run(async () =>
    {
        try
        {
            await SynchronizeRepositoryAsync(
                onlyOutdated,
                token,
                automatic: false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            SetRepositoryState(false, "Script repository operation canceled.");
        }
        catch (Exception exception)
        {
            SetRepositoryState(false, $"Script repository operation failed: {exception.Message}");
        }
    }, CancellationToken.None);

    return BridgeCommandResult.Ok(new
    {
        started = true,
        stats = GetRepositoryStats()
    });
}

BridgeCommandResult StartRepositoryUiRefresh()
{
    if (scriptRepository is null)
    {
        return BridgeCommandResult.Failure("backend-not-ready");
    }

    if (GetRepositoryState().Running || repositoryUiOperationTask is { IsCompleted: false })
    {
        return BridgeCommandResult.Failure("repository-sync-running");
    }

    repositoryUiOperationCts?.Dispose();
    repositoryUiOperationCts =
        CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token);

    CancellationToken token = repositoryUiOperationCts.Token;
    SetRepositoryState(true, "Refreshing script repository...");

    repositoryUiOperationTask = Task.Run(async () =>
    {
        try
        {
            Progress<string> progress = new(message =>
                SetRepositoryState(true, message));

            await scriptRepository.RefreshScriptsAsync(progress, token);
            token.ThrowIfCancellationRequested();

            await ApplyLinuxScriptCompatibilityAsync(token);
            await MirrorDownloadedScriptsToProjectAsync(token);
            SetRepositoryState(false, "Script repository refreshed.");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            SetRepositoryState(false, "Script repository refresh canceled.");
        }
        catch (Exception exception)
        {
            SetRepositoryState(false, $"Script repository refresh failed: {exception.Message}");
        }
    }, CancellationToken.None);

    return BridgeCommandResult.Ok(new
    {
        started = true,
        stats = GetRepositoryStats()
    });
}

BridgeCommandResult CancelRepositoryUiOperation()
{
    if (repositoryUiOperationTask is not { IsCompleted: false } || repositoryUiOperationCts is null)
    {
        SetRepositoryState(false, string.Empty);
        return BridgeCommandResult.Ok(new
        {
            canceled = false,
            stats = GetRepositoryStats()
        });
    }

    repositoryUiOperationCts.Cancel();
    SetRepositoryState(true, "Cancel requested...");
    return BridgeCommandResult.Ok(new
    {
        canceled = true,
        stats = GetRepositoryStats()
    });
}

bool autoCombatTestMode =
    Environment.GetEnvironmentVariable(
        "SKUA_TEST_AUTO_COMBAT"
    ) is string autoCombatValue &&
    (
        autoCombatValue.Equals(
            "1",
            StringComparison.OrdinalIgnoreCase
        ) ||
        autoCombatValue.Equals(
            "true",
            StringComparison.OrdinalIgnoreCase
        )
    );

RuffleBridgeClient bridge =
new();

using IFlashUtil flash =
new RuffleFlashUtil(
    bridge
);

ServiceCollection services =
new();

services.AddSingleton<IFlashUtil>(
    flash
);

services.AddLinuxServices();
services.AddCommonServices();
services.AddScriptableObjects();
services.AddCompiler();

await using ServiceProvider provider =
services.BuildServiceProvider();

Ioc.Default.ConfigureServices(
    provider
);

await using SkuaParityCommandService parityCommands =
new(provider);

async Task<BridgeCommandResult> StartWildcardAutoCombatAsync(
    CancellationToken cancellationToken
)
{
    if (
        scriptAuto is null ||
        scriptCombat is null ||
        scriptPlayer is null ||
        scriptManager is null
    )
    {
        return BridgeCommandResult.Failure(
            "backend-not-ready"
        );
    }

    if (scriptManager.ScriptRunning)
    {
        return BridgeCommandResult.Failure(
            "script-running"
        );
    }

    if (!scriptPlayer.Playing)
    {
        return BridgeCommandResult.Failure(
            "player-not-ready"
        );
    }

    if (scriptAuto.IsRunning)
    {
        return BridgeCommandResult.Ok(
            new
            {
                autoCombatRunning = true,
                mode = "wildcard",
                className =
                    scriptPlayer.CurrentClass?.Name ??
                    "Generic"
            }
        );
    }

    /*
     * O Auto Attack original escolhe o alvo atual
     * quando existe um target vivo. Para garantir o
     * modo wildcard de teste, limpamos o target usando
     * a própria API oficial de combate antes de iniciar.
     */
    scriptCombat.StopAttacking = false;
    scriptCombat.CancelAutoAttack();

    for (int attempt = 0; attempt < 10; attempt++)
    {
        if (!scriptPlayer.HasTarget)
        {
            break;
        }

        scriptCombat.CancelTarget();

        await Task.Delay(
            50,
            cancellationToken
        );
    }

    if (scriptPlayer.HasTarget)
    {
        return BridgeCommandResult.Failure(
            "target-could-not-be-cleared"
        );
    }

    scriptAuto.StartAutoAttack();

    await Task.Delay(
        100,
        cancellationToken
    );

    if (!scriptAuto.IsRunning)
    {
        return BridgeCommandResult.Failure(
            "auto-combat-not-started"
        );
    }

    string className =
        scriptPlayer.CurrentClass?.Name ??
        "Generic";

    Console.WriteLine(
        "Auto combat de teste iniciado: " +
        $"wildcard, classe={className}."
    );

    return BridgeCommandResult.Ok(
        new
        {
            autoCombatRunning = true,
            mode = "wildcard",
            className
        }
    );
}

async Task<BridgeCommandResult> StopAutoCombatAsync()
{
    if (scriptAuto is null)
    {
        return BridgeCommandResult.Failure(
            "backend-not-ready"
        );
    }

    await scriptAuto.StopAsync();

    Console.WriteLine(
        "Auto combat de teste encerrado."
    );

    return BridgeCommandResult.Ok(
        new
        {
            autoCombatRunning =
                scriptAuto.IsRunning
        }
    );
}

bridge.CommandHandler = async (
    command,
    cancellationToken
) =>
{
    if (
        !string.Equals(
            command.Name,
            "notify.poll",
            StringComparison.Ordinal
        ) &&
        !string.Equals(
            command.Name,
            "backend.status",
            StringComparison.Ordinal
        )
    )
    {
        Console.WriteLine(
            $"Comando da interface: {command.Name}"
        );
    }

    switch (command.Name)
    {
        case "backend.status":
            return BridgeCommandResult.Ok(
                new
                {
                    ready = backendReady,
                    phase = backendPhase
                }
            );

        case "script.status":
        {
            if (scriptManager is null)
            {
                return BridgeCommandResult.Failure(
                    "backend-not-ready"
                );
            }

            string loadedScript =
                scriptManager.LoadedScript;

            return BridgeCommandResult.Ok(
                new
                {
                    scriptRunning =
                        scriptManager.ScriptRunning,
                    scriptPaused,
                    pauseSemantics =
                        "restart-on-resume",
                    loadedScript,
                    loadedScriptName =
                        string.IsNullOrWhiteSpace(
                            loadedScript
                        )
                            ? string.Empty
                            : Path.GetFileName(
                                loadedScript
                            ),
                    scriptsDirectory =
                        projectScriptsDirectory,
                    repository =
                        GetRepositoryStats()
                }
            );
        }

        case "script.load":
        {
            if (scriptManager is null)
            {
                return BridgeCommandResult.Failure(
                    "backend-not-ready"
                );
            }

            if (scriptManager.ScriptRunning)
            {
                return BridgeCommandResult.Failure(
                    "script-running"
                );
            }

            string? rawPath =
                GetCommandString(command, 0);

            if (string.IsNullOrWhiteSpace(rawPath))
            {
                return BridgeCommandResult.Failure(
                    "script-path-required"
                );
            }

            string fullPath =
                Path.GetFullPath(rawPath);

            if (
                !File.Exists(fullPath) ||
                !string.Equals(
                    Path.GetExtension(fullPath),
                    ".cs",
                    StringComparison.OrdinalIgnoreCase
                )
            )
            {
                return BridgeCommandResult.Failure(
                    "script-file-not-found"
                );
            }

            scriptManager.SetLoadedScript(fullPath);
            scriptPaused = false;
            pausedScriptPath = string.Empty;

            Console.WriteLine(
                $"Script carregado: {fullPath}"
            );

            return BridgeCommandResult.Ok(
                new
                {
                    loadedScript = fullPath,
                    loadedScriptName =
                        Path.GetFileName(fullPath),
                    scriptRunning = false,
                    scriptPaused = false
                }
            );
        }

        case "script.start":
        {
            if (scriptManager is null)
            {
                return BridgeCommandResult.Failure(
                    "backend-not-ready"
                );
            }

            if (string.IsNullOrWhiteSpace(
                scriptManager.LoadedScript
            ))
            {
                return BridgeCommandResult.Failure(
                    "no-script-loaded"
                );
            }

            await ApplyLinuxScriptCompatibilityAsync(
                cancellationToken
            );

            scriptPaused = false;
            pausedScriptPath = string.Empty;

            Exception? error =
                await scriptManager.StartScript();

            if (error is not null)
            {
                return BridgeCommandResult.Failure(
                    error.ToString()
                );
            }

            return BridgeCommandResult.Ok(
                new
                {
                    scriptRunning = true,
                    scriptPaused = false,
                    loadedScript =
                        scriptManager.LoadedScript,
                    loadedScriptName =
                        Path.GetFileName(
                            scriptManager.LoadedScript
                        )
                }
            );
        }

        case "script.stop":
        {
            if (scriptManager is null)
            {
                return BridgeCommandResult.Failure(
                    "backend-not-ready"
                );
            }

            await scriptManager.StopScript();
            scriptPaused = false;
            pausedScriptPath = string.Empty;

            return BridgeCommandResult.Ok(
                new
                {
                    scriptRunning = false,
                    scriptPaused = false
                }
            );
        }

        case "script.pause":
        {
            if (scriptManager is null)
            {
                return BridgeCommandResult.Failure(
                    "backend-not-ready"
                );
            }

            if (scriptPaused)
            {
                return BridgeCommandResult.Ok(
                    new
                    {
                        scriptRunning = false,
                        scriptPaused = true,
                        pauseSemantics =
                            "restart-on-resume"
                    }
                );
            }

            if (!scriptManager.ScriptRunning)
            {
                return BridgeCommandResult.Failure(
                    "script-not-running"
                );
            }

            pausedScriptPath =
                scriptManager.LoadedScript;

            /*
             * O ScriptManager original não possui pausa geral
             * cooperativa. Para evitar Thread.Suspend/estado inseguro,
             * a pausa Linux para o script e Resume o inicia novamente
             * do começo, preservando o arquivo selecionado.
             */
            await scriptManager.StopScript(false);
            scriptPaused = true;

            return BridgeCommandResult.Ok(
                new
                {
                    scriptRunning = false,
                    scriptPaused = true,
                    pauseSemantics =
                        "restart-on-resume"
                }
            );
        }

        case "script.resume":
        {
            if (scriptManager is null)
            {
                return BridgeCommandResult.Failure(
                    "backend-not-ready"
                );
            }

            if (!scriptPaused)
            {
                return BridgeCommandResult.Failure(
                    "script-not-paused"
                );
            }

            string resumePath =
                string.IsNullOrWhiteSpace(
                    pausedScriptPath
                )
                    ? scriptManager.LoadedScript
                    : pausedScriptPath;

            if (!File.Exists(resumePath))
            {
                return BridgeCommandResult.Failure(
                    "paused-script-file-not-found"
                );
            }

            scriptManager.SetLoadedScript(resumePath);

            await ApplyLinuxScriptCompatibilityAsync(
                cancellationToken
            );

            Exception? error =
                await scriptManager.StartScript();

            if (error is not null)
            {
                return BridgeCommandResult.Failure(
                    error.ToString()
                );
            }

            scriptPaused = false;
            pausedScriptPath = string.Empty;

            return BridgeCommandResult.Ok(
                new
                {
                    scriptRunning = true,
                    scriptPaused = false,
                    resumedFromStart = true
                }
            );
        }

        case "script.repo.status":
            return scriptRepository is null
                ? BridgeCommandResult.Failure(
                    "backend-not-ready"
                )
                : BridgeCommandResult.Ok(
                    GetRepositoryStats()
                );

        case "script.repo.search":
        {
            if (scriptRepository is null)
            {
                return BridgeCommandResult.Failure(
                    "backend-not-ready"
                );
            }

            string query =
                GetCommandString(command, 0)?.Trim() ??
                string.Empty;

            int limit = Math.Clamp(
                GetCommandInt(command, 1, 80),
                1,
                200
            );

            /*
             * Mirror ScriptRepoViewModel.RefreshScriptsList():
             * the upstream manifest contains placeholder entries whose
             * Name/Description/Tags are the literal string "null".
             * The Windows UI filters/normalizes them before rendering.
             */
            IEnumerable<ScriptInfo> source =
                scriptRepository.Scripts
                .Where(script =>
                    script is not null &&
                    script.Name is not null &&
                    !script.Name.Equals("null")
                )
                .Select(script =>
                {
                    if (
                        script.Description?.Equals(
                            "null"
                        ) == true
                    )
                    {
                        script.Description =
                            "No description provided.";
                    }

                    if (
                        script.Tags?.Contains(
                            "null"
                        ) == true &&
                        script.Tags.Length == 1
                    )
                    {
                        script.Tags =
                            new[] { "no-tags" };
                    }
                    else
                    {
                        script.Tags ??=
                            new[] { "no-tags" };
                    }

                    return script;
                });

            if (!string.IsNullOrWhiteSpace(query))
            {
                source = source.Where(script =>
                    (script.Name?.Contains(
                        query,
                        StringComparison.OrdinalIgnoreCase
                    ) ?? false) ||
                    (script.Description?.Contains(
                        query,
                        StringComparison.OrdinalIgnoreCase
                    ) ?? false) ||
                    (script.FilePath?.Contains(
                        query,
                        StringComparison.OrdinalIgnoreCase
                    ) ?? false) ||
                    (script.Tags?.Any(tag =>
                        tag.Contains(
                            query,
                            StringComparison.OrdinalIgnoreCase
                        )
                    ) ?? false)
                );
            }

            List<ScriptInfo> filtered =
                source.Take(limit).ToList();

            return BridgeCommandResult.Ok(
                new
                {
                    query,
                    scripts = filtered.Select(script =>
                        new
                        {
                            name = script.Name,
                            description =
                                script.Description,
                            tags =
                                script.Tags ??
                                Array.Empty<string>(),
                            filePath =
                                script.FilePath,
                            fileName =
                                script.FileName,
                            size = script.Size,
                            downloaded =
                                script.Downloaded,
                            outdated =
                                script.Downloaded &&
                                IsScriptOutdatedForLinux(
                                    script
                                ),
                            runtimePath =
                                script.LocalFile,
                            projectPath =
                                GetProjectScriptPath(
                                    script
                                )
                        }
                    ).ToList(),
                    stats = GetRepositoryStats()
                }
            );
        }

        case "script.repo.refresh":
            return StartRepositoryUiRefresh();

        case "script.repo.cancel":
            return CancelRepositoryUiOperation();

        case "script.repo.download":
        {
            if (scriptRepository is null)
            {
                return BridgeCommandResult.Failure(
                    "backend-not-ready"
                );
            }

            if (GetRepositoryState().Running)
            {
                return BridgeCommandResult.Failure(
                    "repository-sync-running"
                );
            }

            string? filePath =
                GetCommandString(command, 0);

            ScriptInfo? info =
                scriptRepository.Scripts.FirstOrDefault(
                    script => string.Equals(
                        script.FilePath,
                        filePath,
                        StringComparison.OrdinalIgnoreCase
                    )
                );

            if (info is null)
            {
                return BridgeCommandResult.Failure(
                    "script-not-found"
                );
            }

            await scriptRepository.DownloadScriptAsync(
                info
            );

            await ApplyLinuxCoreBotsCompatibilityAsync(
                info,
                cancellationToken
            );

            await MirrorScriptToProjectAsync(
                info,
                cancellationToken
            );

            return BridgeCommandResult.Ok(
                new
                {
                    downloaded = true,
                    runtimePath = info.LocalFile,
                    projectPath =
                        GetProjectScriptPath(info),
                    stats = GetRepositoryStats()
                }
            );
        }

        case "script.repo.downloadAll":
            return StartRepositoryUiSync(false);

        case "script.repo.updateAll":
            return StartRepositoryUiSync(true);

        case "bank.open":
        {
            if (scriptBank is null)
            {
                return BridgeCommandResult.Failure(
                    "backend-not-ready"
                );
            }

            scriptBank.Open();

            return BridgeCommandResult.Ok(
                new
                {
                    opened = true,
                    loaded = scriptBank.Loaded
                }
            );
        }

        case "jump.status":
        {
            if (
                mapService is null ||
                scriptPlayer is null
            )
            {
                return BridgeCommandResult.Failure(
                    "backend-not-ready"
                );
            }

            return BridgeCommandResult.Ok(
                new
                {
                    map = mapService.MapName,
                    cell = mapService.Cell,
                    pad = mapService.Pad,
                    cells = mapService.Cells,
                    pads = mapService.Pads
                }
            );
        }

        case "jump.execute":
        {
            if (
                mapService is null ||
                scriptPlayer is null
            )
            {
                return BridgeCommandResult.Failure(
                    "backend-not-ready"
                );
            }

            string cell =
                GetCommandString(command, 0) ??
                scriptPlayer.Cell;

            string pad =
                GetCommandString(command, 1) ??
                scriptPlayer.Pad;

            if (string.IsNullOrWhiteSpace(cell))
            {
                return BridgeCommandResult.Failure(
                    "cell-required"
                );
            }

            mapService.Jump(cell, pad);

            return BridgeCommandResult.Ok(
                new
                {
                    map = mapService.MapName,
                    cell,
                    pad
                }
            );
        }

        case "travel.join":
        {
            if (
                mapService is null ||
                scriptPlayer is null
            )
            {
                return BridgeCommandResult.Failure(
                    "backend-not-ready"
                );
            }

            string? mapName =
                GetCommandString(command, 0);

            string cell =
                GetCommandString(command, 1) ??
                "Enter";

            string pad =
                GetCommandString(command, 2) ??
                "Spawn";

            int privateNumber =
                GetCommandInt(command, 3, 0);

            if (string.IsNullOrWhiteSpace(mapName))
            {
                return BridgeCommandResult.Failure(
                    "map-required"
                );
            }

            string destination =
                privateNumber > 0 &&
                !mapName.Contains('-')
                    ? $"{mapName}-{privateNumber}"
                    : mapName;

            IScriptMap scriptMapService =
                provider.GetRequiredService<IScriptMap>();

            scriptMapService.Join(
                destination,
                cell,
                pad
            );

            return BridgeCommandResult.Ok(
                new
                {
                    map = destination,
                    cell,
                    pad
                }
            );
        }

        case "combat.auto.status":
            return scriptAuto is null
                ? BridgeCommandResult.Failure(
                    "backend-not-ready"
                )
                : BridgeCommandResult.Ok(
                    new
                    {
                        autoCombatRunning =
                            scriptAuto.IsRunning
                    }
                );

        case "combat.auto.start":
            return await StartWildcardAutoCombatAsync(
                cancellationToken
            );

        case "combat.auto.stop":
            return await StopAutoCombatAsync();

        default:
        {
            BridgeCommandResult? parityResult =
                await parityCommands.HandleAsync(
                    command,
                    cancellationToken
                );

            return parityResult ??
                BridgeCommandResult.Failure(
                    $"Comando desconhecido: {command.Name}"
                );
        }
    }
};

flash.FlashCall += (
    function,
    args
) =>
{
    if (function == "loaded")
    {
        Console.WriteLine(
            "Evento oficial FlashCall: loaded"
        );

        gameLoaded.TrySetResult(true);
    }
};

try
{
    Console.WriteLine(
        "Testando configurações Linux..."
    );

    Console.WriteLine(
        $"SkuaDIR: {ClientFileSources.SkuaDIR}"
    );

    Console.WriteLine(
        $"SkuaScriptsDIR: " +
        $"{ClientFileSources.SkuaScriptsDIR}"
    );

    ISettingsService settings =
    provider.GetRequiredService<ISettingsService>();

    int animationFrameRate =
    settings.Get(
        "AnimationFrameRate",
        60
    );

    Console.WriteLine(
        $"AnimationFrameRate lido: " +
        $"{animationFrameRate}"
    );

    settings.Set(
        "AnimationFrameRate",
        animationFrameRate
    );

    Console.WriteLine(
        "Configurações lidas e gravadas com sucesso."
    );

    backendPhase = "initializing-flash";
    flash.InitializeFlash();

    backendPhase = "waiting-for-game";
    Console.WriteLine(
        "Aguardando o jogo..."
    );

    /*
     * O renderer pode já ter enviado `loaded` antes de o
     * backend terminar de registrar o host. O main.js mantém
     * esse evento em cache, mas uma corrida de inicialização
     * não deve ser capaz de derrubar o serviço permanente.
     *
     * IFlashUtil.IsWorldLoaded é o segundo sinal oficial de
     * prontidão do Core. Se o world já existe, seguimos sem
     * depender do callback histórico. Caso contrário,
     * aguardamos `loaded` sem timeout artificial; Ctrl+C ainda
     * cancela normalmente pelo token de shutdown.
     */
    if (flash.IsWorldLoaded)
    {
        Console.WriteLine(
            "World já está carregado; " +
            "seguindo sem aguardar o evento loaded."
        );

        gameLoaded.TrySetResult(true);
    }
    else
    {
        await gameLoaded.Task.WaitAsync(
            shutdown.Token
        );
    }

    backendPhase = "resolving-services";

    scriptManager =
    provider.GetRequiredService<IScriptManager>();

    scriptInterfaceManager =
    (IScriptInterfaceManager)
    provider.GetRequiredService<IScriptInterface>();

    /*
     * The original Windows application initializes ScriptInterface as part of
     * application startup.  Its timer is not script-specific: it owns
     * catchPackets and CheckOptions(), which applies AggroMonsters,
     * AggroAllMonsters, Magnetise, InfiniteRange, SkipCutscenes, WalkSpeed,
     * RestPackets and related persistent game options.
     *
     * The Linux service previously resolved IScriptInterface but never called
     * Initialize(), so those options could be saved successfully while never
     * being applied to the live game.  Start the same Core timer here.
     */
    scriptInterfaceManager.Initialize();
    Console.WriteLine(
        "ScriptInterface timer inicializado."
    );

    Console.WriteLine(
        "Resolvendo estado do jogo pelo Skua.Core..."
    );

    IScriptMap scriptMap =
    provider.GetRequiredService<IScriptMap>();

    scriptPlayer =
    provider.GetRequiredService<IScriptPlayer>();

    scriptAuto =
    provider.GetRequiredService<IScriptAuto>();

    scriptCombat =
    provider.GetRequiredService<IScriptCombat>();

    scriptBank =
    provider.GetRequiredService<IScriptBank>();

    mapService =
    provider.GetRequiredService<IMapService>();

    scriptRepository =
    provider.GetRequiredService<IGetScriptsService>();

    Console.WriteLine(
        $"Projeto Skua Linux: {projectRoot}"
    );

    Console.WriteLine(
        $"Espelho de scripts: {projectScriptsDirectory}"
    );

    try
    {
        Progress<string> repositoryProgress =
            new(message =>
                SetRepositoryState(
                    false,
                    message
                )
            );

        backendPhase = "loading-script-repository";

        await scriptRepository.GetScriptsAsync(
            repositoryProgress,
            shutdown.Token
        );

        await ApplyLinuxScriptCompatibilityAsync(
            shutdown.Token
        );

        await MirrorDownloadedScriptsToProjectAsync(
            shutdown.Token
        );

        _ = Task.Run(
            async () =>
            {
                try
                {
                    await SynchronizeRepositoryAsync(
                        false,
                        CancellationToken.None,
                        automatic: true
                    );
                }
                catch (Exception exception)
                {
                    SetRepositoryState(
                        false,
                        $"Automatic script sync failed: {exception.Message}"
                    );
                }
            }
        );
    }
    catch (Exception exception)
    {
        SetRepositoryState(
            false,
            $"Repository manifest unavailable: {exception.Message}"
        );
    }

    Console.WriteLine(
        $"Mapa pelo IScriptMap: {scriptMap.Name}"
    );

    Console.WriteLine(
        $"Célula pelo IScriptPlayer: " +
        $"{scriptPlayer.Cell}"
    );

    Console.WriteLine(
        $"World carregado: " +
        $"{flash.IsWorldLoaded}"
    );

    bool loggedIn =
    flash.Call<bool>(
        "isLoggedIn"
    );

    Console.WriteLine(
        $"Personagem conectado: " +
        $"{loggedIn}"
    );

    string? username =
    flash.GetGameObject<string>(
        "world.myAvatar.objData.strUsername"
    );

    string? map =
    flash.GetGameObject<string>(
        "world.strMapName"
    );

    string? cell =
    flash.GetGameObject<string>(
        "world.strFrame"
    );

    Console.WriteLine(
        $"Usuário: {username}"
    );

    Console.WriteLine(
        $"Mapa: {map}"
    );

    Console.WriteLine(
        $"Célula: {cell}"
    );

    double x =
    flash.GetGameObject<double>(
        "world.myAvatar.pMC.x"
    );

    double y =
    flash.GetGameObject<double>(
        "world.myAvatar.pMC.y"
    );

    Console.WriteLine(
        $"Posição: X={x}, Y={y}"
    );

    Console.WriteLine();

    backendPhase = "ready";
    backendReady = true;

    Console.WriteLine(
        "Backend pronto."
    );

    Console.WriteLine(
        "Nenhum script foi iniciado automaticamente."
    );

    Console.WriteLine(
        "Sincronização automática do repositório de scripts foi iniciada em segundo plano."
    );

    if (autoCombatTestMode)
    {
        Console.WriteLine(
            "SKUA_TEST_AUTO_COMBAT ativo; " +
            "iniciando Auto Attack wildcard..."
        );

        BridgeCommandResult autoCombatResult =
            await StartWildcardAutoCombatAsync(
                shutdown.Token
            );

        if (!autoCombatResult.Success)
        {
            Console.Error.WriteLine(
                "Falha ao iniciar Auto Attack de teste: " +
                autoCombatResult.Error
            );
        }
    }

    Console.WriteLine(
        "Aguardando comandos da interface do Aquastar..."
    );

    Console.WriteLine(
        "Pressione Ctrl+C para encerrar."
    );

    await Task.Delay(
        Timeout.InfiniteTimeSpan,
        shutdown.Token
    );
}
catch (OperationCanceledException)
    when (shutdown.IsCancellationRequested)
{
    Console.WriteLine(
        "Encerramento solicitado."
    );
}
catch (Exception exception)
{
    Console.Error.WriteLine(
        "Falha no serviço do Skua:"
    );

    Console.Error.WriteLine(
        exception
    );

    Environment.ExitCode = 1;
}
finally
{
    backendReady = false;
    backendPhase = "shutting-down";

    Console.WriteLine(
        "Encerrando os serviços do Skua..."
    );

    if (scriptAuto is not null)
    {
        try
        {
            await scriptAuto.StopAsync();

            Console.WriteLine(
                "IScriptAuto.StopAsync concluído."
            );
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(
                "Falha ao parar o Auto Attack:"
            );

            Console.Error.WriteLine(
                exception
            );
        }
    }

    if (scriptManager is not null)
    {
        try
        {
            await scriptManager.StopScript();

            Console.WriteLine(
                "ScriptManager.StopScript concluído."
            );
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(
                "Falha ao parar o script:"
            );

            Console.Error.WriteLine(
                exception
            );
        }
    }

    if (scriptInterfaceManager is not null)
    {
        try
        {
            await scriptInterfaceManager
            .StopTimerAsync();

            Console.WriteLine(
                "Temporizador da interface encerrado."
            );
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(
                "Falha ao encerrar o temporizador:"
            );

            Console.Error.WriteLine(
                exception
            );
        }
    }

    Console.WriteLine(
        "Backend encerrado graciosamente."
    );
}
