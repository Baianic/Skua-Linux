using CommunityToolkit.Mvvm.Messaging;
using Newtonsoft.Json;
using Skua.Core.Flash;
using Skua.Core.Interfaces;
using Skua.Core.Messaging;
using Skua.Core.Utils;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Dynamic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using SystemTextJsonSerializer = System.Text.Json.JsonSerializer;

namespace Skua.WPF.Flash;

/// <summary>
/// Implementação de IFlashUtil que se comunica com o Ruffle/Electron
/// por meio da ponte WebSocket local.
/// </summary>
public sealed class RuffleFlashUtil : IFlashUtil
{
    private const string BridgeUrl = "ws://127.0.0.1:8182";

    private static readonly TimeSpan ConnectTimeout =
        TimeSpan.FromSeconds(10);

    private static readonly TimeSpan CallTimeout =
        TimeSpan.FromSeconds(15);

    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(
            Environment.SpecialFolder.ApplicationData
        ),
        "Skua",
        "ruffle-bridge.log"
    );

    private static readonly object LogLock = new();

    private readonly IMessenger _messenger;
    private readonly Lazy<IScriptManager> _lazyManager;

    private readonly ConcurrentDictionary<
        long,
        TaskCompletionSource<BridgeResult>
    > _pendingCalls = new();

    private readonly SemaphoreSlim _sendLock = new(1, 1);

    private readonly object _connectionLock = new();

    private ClientWebSocket? _socket;
    private CancellationTokenSource? _disposeCts;
    private Task? _receiveTask;

    private TaskCompletionSource<bool>?
        _helloAcknowledged;

    private long _nextRequestId;
    private bool _disposed;

    public RuffleFlashUtil(
        IMessenger messenger,
        Lazy<IScriptManager> manager
    )
    {
        _messenger = messenger;
        _lazyManager = manager;
    }

    public event FlashCallHandler? FlashCall;

    public void InitializeFlash()
    {
        ThrowIfDisposed();

        lock (_connectionLock)
        {
            if (
                _socket is not null &&
                _socket.State == WebSocketState.Open
            )
            {
                return;
            }

            CleanupConnection();

            _disposeCts =
                new CancellationTokenSource();

            _socket =
                new ClientWebSocket();

            _helloAcknowledged =
                new TaskCompletionSource<bool>(
                    TaskCreationOptions
                        .RunContinuationsAsynchronously
                );

            try
            {
                Log(
                    $"Conectando em {BridgeUrl}"
                );

                using CancellationTokenSource connectCts =
                    CancellationTokenSource
                        .CreateLinkedTokenSource(
                            _disposeCts.Token
                        );

                connectCts.CancelAfter(
                    ConnectTimeout
                );

                _socket
                    .ConnectAsync(
                        new Uri(BridgeUrl),
                        connectCts.Token
                    )
                    .GetAwaiter()
                    .GetResult();

                Log("WebSocket conectado");

                _receiveTask = Task.Run(
                    () => ReceiveLoopAsync(
                        _disposeCts.Token
                    )
                );

                SendMessageAsync(
                    new
                    {
                        type = "hello",
                        role = "host"
                    },
                    _disposeCts.Token
                )
                .GetAwaiter()
                .GetResult();

                bool acknowledged =
                    _helloAcknowledged.Task
                        .Wait(
                            ConnectTimeout
                        );

                if (!acknowledged)
                {
                    throw new TimeoutException(
                        "A ponte não confirmou o registro " +
                        "do host dentro do tempo limite."
                    );
                }

                Log(
                    "Host registrado na ponte"
                );
            }
            catch (Exception ex)
            {
                Log(
                    "Erro durante InitializeFlash:" +
                    Environment.NewLine +
                    ex
                );

                CleanupConnection();

                _messenger.Send<FlashErrorMessage>(
                    new(
                        ex,
                        "InitializeFlash",
                        Array.Empty<object>()
                    )
                );

                throw;
            }
        }
    }

    public string? Call(
        string function,
        params object[] args
    )
    {
        return Call<string?>(
            function,
            args
        );
    }

    public T? Call<T>(
        string function,
        params object[] args
    )
    {
        try
        {
            object? result = Call(
                function,
                typeof(T),
                args
            );

            if (result is null)
            {
                return (T?)DefaultProvider
                    .GetDefault<T>(
                        typeof(T)
                    );
            }

            return (T)result;
        }
        catch
        {
            return (T?)DefaultProvider
                .GetDefault<T>(
                    typeof(T)
                );
        }
    }

    public object? Call(
        string function,
        Type type,
        params object[] args
    )
    {
        ThrowIfDisposed();

        if (
            _lazyManager.Value.ShouldExit &&
            Thread.CurrentThread.Name ==
                "Script Thread"
        )
        {
            _lazyManager.Value
                .ScriptCts?
                .Token
                .ThrowIfCancellationRequested();
        }

        try
        {
            EnsureConnected();

            long id =
                Interlocked.Increment(
                    ref _nextRequestId
                );

            TaskCompletionSource<BridgeResult> completion =
                new(
                    TaskCreationOptions
                        .RunContinuationsAsynchronously
                );

            if (
                !_pendingCalls.TryAdd(
                    id,
                    completion
                )
            )
            {
                throw new InvalidOperationException(
                    $"Não foi possível registrar " +
                    $"a chamada {id}."
                );
            }

            Log(
                $"C# -> Ruffle: #{id} " +
                $"{function} args=" +
                JsonConvert.SerializeObject(args)
            );

            try
            {
                SendMessageAsync(
                    new
                    {
                        type = "call",
                        id,
                        function,
                        args
                    },
                    _disposeCts!.Token
                )
                .GetAwaiter()
                .GetResult();

                bool completed =
                    completion.Task.Wait(
                        CallTimeout
                    );

                if (!completed)
                {
                    throw new TimeoutException(
                        $"A chamada '{function}' " +
                        $"excedeu {CallTimeout.TotalSeconds} " +
                        "segundos."
                    );
                }

                BridgeResult response =
                    completion.Task
                        .GetAwaiter()
                        .GetResult();

                if (!response.Success)
                {
                    throw new InvalidOperationException(
                        response.Error ??
                        $"A chamada '{function}' falhou."
                    );
                }

                Log(
                    $"Ruffle -> C#: #{id} " +
                    $"{function} = " +
                    $"{response.RawResult ?? "<null>"}"
                );

                return ConvertResult(
                    response.RawResult,
                    type
                );
            }
            finally
            {
                _pendingCalls.TryRemove(
                    id,
                    out _
                );
            }
        }
        catch (Exception ex)
        {
            Log(
                $"Erro na chamada '{function}':" +
                Environment.NewLine +
                ex
            );

            _messenger.Send<FlashErrorMessage>(
                new(
                    ex,
                    function,
                    args
                )
            );

            return type.IsValueType
            ? Activator.CreateInstance(type)
            : null;
        }
    }

    private void EnsureConnected()
    {
        if (
            _socket is not null &&
            _socket.State ==
                WebSocketState.Open
        )
        {
            return;
        }

        InitializeFlash();
    }

    private async Task SendMessageAsync(
        object message,
        CancellationToken cancellationToken
    )
    {
        ClientWebSocket socket =
            _socket ??
            throw new InvalidOperationException(
                "A ponte WebSocket não foi inicializada."
            );

        string json =
            SystemTextJsonSerializer.Serialize(
                message
            );

        byte[] bytes =
            Encoding.UTF8.GetBytes(json);

        await _sendLock.WaitAsync(
            cancellationToken
        );

        try
        {
            if (
                socket.State !=
                WebSocketState.Open
            )
            {
                throw new WebSocketException(
                    "A conexão com a ponte não está aberta."
                );
            }

            await socket.SendAsync(
                new ArraySegment<byte>(bytes),
                WebSocketMessageType.Text,
                true,
                cancellationToken
            );
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task ReceiveLoopAsync(
        CancellationToken cancellationToken
    )
    {
        byte[] buffer =
            new byte[16 * 1024];

        try
        {
            while (
                !cancellationToken
                    .IsCancellationRequested &&
                _socket is not null &&
                _socket.State ==
                    WebSocketState.Open
            )
            {
                using MemoryStream messageStream =
                    new();

                WebSocketReceiveResult result;

                do
                {
                    result =
                        await _socket.ReceiveAsync(
                            new ArraySegment<byte>(
                                buffer
                            ),
                            cancellationToken
                        );

                    if (
                        result.MessageType ==
                        WebSocketMessageType.Close
                    )
                    {
                        Log(
                            "A ponte solicitou o encerramento."
                        );

                        return;
                    }

                    messageStream.Write(
                        buffer,
                        0,
                        result.Count
                    );
                }
                while (!result.EndOfMessage);

                string json =
                    Encoding.UTF8.GetString(
                        messageStream.ToArray()
                    );

                ProcessIncomingMessage(json);
            }
        }
        catch (
            OperationCanceledException
        ) when (
            cancellationToken
                .IsCancellationRequested
        )
        {
            // Encerramento normal.
        }
        catch (Exception ex)
        {
            Log(
                "Erro no loop de recebimento:" +
                Environment.NewLine +
                ex
            );

            FailPendingCalls(ex);
        }
    }

    private void ProcessIncomingMessage(
        string json
    )
    {
        try
        {
            using JsonDocument document =
                JsonDocument.Parse(json);

            JsonElement root =
                document.RootElement;

            if (
                !root.TryGetProperty(
                    "type",
                    out JsonElement typeElement
                )
            )
            {
                return;
            }

            string? messageType =
                typeElement.GetString();

            switch (messageType)
            {
                case "hello-ack":
                    _helloAcknowledged?
                        .TrySetResult(true);

                    break;

                case "result":
                    ProcessResult(root);
                    break;

                case "event":
                    ProcessEvent(root);
                    break;

                case "error":
                    Log(
                        "Erro recebido da ponte: " +
                        json
                    );
                    break;

                default:
                    Log(
                        "Mensagem desconhecida: " +
                        json
                    );
                    break;
            }
        }
        catch (Exception ex)
        {
            Log(
                "Falha ao processar mensagem:" +
                Environment.NewLine +
                json +
                Environment.NewLine +
                ex
            );
        }
    }

    private void ProcessResult(
        JsonElement root
    )
    {
        if (
            !root.TryGetProperty(
                "id",
                out JsonElement idElement
            ) ||
            !idElement.TryGetInt64(
                out long id
            )
        )
        {
            return;
        }

        if (
            !_pendingCalls.TryGetValue(
                id,
                out TaskCompletionSource<
                    BridgeResult
                >? completion
            )
        )
        {
            return;
        }

        bool success =
            root.TryGetProperty(
                "success",
                out JsonElement successElement
            ) &&
            successElement.ValueKind ==
                JsonValueKind.True;

        string? error = null;

        if (
            root.TryGetProperty(
                "error",
                out JsonElement errorElement
            ) &&
            errorElement.ValueKind ==
                JsonValueKind.String
        )
        {
            error =
                errorElement.GetString();
        }

        string? rawResult = null;

        if (
            root.TryGetProperty(
                "result",
                out JsonElement resultElement
            ) &&
            resultElement.ValueKind !=
                JsonValueKind.Null &&
            resultElement.ValueKind !=
                JsonValueKind.Undefined
        )
        {
            rawResult =
                resultElement.ValueKind ==
                    JsonValueKind.String
                ? resultElement.GetString()
                : resultElement.GetRawText();
        }

        completion.TrySetResult(
            new BridgeResult(
                success,
                rawResult,
                error
            )
        );
    }

    private void ProcessEvent(
        JsonElement root
    )
    {
        if (
            !root.TryGetProperty(
                "name",
                out JsonElement nameElement
            )
        )
        {
            return;
        }

        string? name =
            nameElement.GetString();

        if (
            string.IsNullOrWhiteSpace(name)
        )
        {
            return;
        }

        object[] args =
            Array.Empty<object>();

        if (
            root.TryGetProperty(
                "args",
                out JsonElement argsElement
            ) &&
            argsElement.ValueKind ==
                JsonValueKind.Array
        )
        {
            args =
                argsElement
                    .EnumerateArray()
                    .Select(
                        ConvertJsonElement
                    )
                    .ToArray();
        }

        Log(
            $"Evento Ruffle -> C#: " +
            $"{name} ({args.Length} argumentos)"
        );
        var handlers = FlashCall;

        if (handlers is null)
        {
            return;
        }

        _ = Task.Run(() =>
        {
            try
            {
                handlers.Invoke(
                    name,
                    args
                );
            }
            catch (Exception ex)
            {
                Log(
                    $"Erro em handler do evento '{name}':" +
                    Environment.NewLine +
                    ex
                );
            }
        });
    }

    private static object ConvertJsonElement(
        JsonElement element
    )
    {
        return element.ValueKind switch
        {
            JsonValueKind.String =>
                element.GetString() ?? string.Empty,

            JsonValueKind.Number =>
                element.TryGetInt32(out int i)
                    ? i
                    : element.TryGetInt64(out long l)
                        ? l
                        : element.GetDouble(),

            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null!,

            JsonValueKind.Array =>
                element
                    .EnumerateArray()
                    .Select(ConvertJsonElement)
                    .ToArray(),

            JsonValueKind.Object =>
                JsonConvert.DeserializeObject<
                    ExpandoObject
                >(
                    element.GetRawText()
                )!,

            _ => element.GetRawText()
        };
    }

    private static object? ConvertResult(
        string? rawResult,
        Type targetType
    )
    {
        Type effectiveType =
            Nullable.GetUnderlyingType(
                targetType
            ) ?? targetType;

        if (rawResult is null)
        {
            return targetType.IsValueType
            ? Activator.CreateInstance(targetType)
            : null;
        }

        if (
            effectiveType ==
            typeof(string)
        )
        {
            return rawResult;
        }

        if (
            effectiveType ==
            typeof(bool)
        )
        {
            if (
                bool.TryParse(
                    rawResult,
                    out bool boolResult
                )
            )
            {
                return boolResult;
            }

            if (
                int.TryParse(
                    rawResult,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out int numericBool
                )
            )
            {
                return numericBool != 0;
            }
        }

        if (
            effectiveType.IsEnum
        )
        {
            return Enum.Parse(
                effectiveType,
                rawResult,
                true
            );
        }

        try
        {
            return Convert.ChangeType(
                rawResult,
                effectiveType,
                CultureInfo.InvariantCulture
            );
        }
        catch
        {
            return JsonConvert.DeserializeObject(
                rawResult,
                effectiveType
            );
        }
    }

    public object FromFlashXml(
        XElement element
    )
    {
        switch (
            element.Name.ToString()
        )
        {
            case "number":
                return int.TryParse(
                    element.Value,
                    out int integer
                )
                    ? integer
                    : float.TryParse(
                        element.Value,
                        NumberStyles.Float,
                        CultureInfo.InvariantCulture,
                        out float number
                    )
                        ? number
                        : 0;

            case "true":
                return true;

            case "false":
                return false;

            case "null":
                return null!;

            case "array":
                return element
                    .Elements()
                    .Select(FromFlashXml)
                    .ToArray();

            case "object":
                dynamic result =
                    new ExpandoObject();

                foreach (
                    XElement child
                    in element.Elements()
                )
                {
                    string key =
                        child
                            .Attribute("id")!
                            .Value;

                    XElement? valueElement =
                        child.Elements()
                            .FirstOrDefault();

                    result[key] =
                        valueElement is null
                            ? null
                            : FromFlashXml(
                                valueElement
                            );
                }

                return result;

            default:
                return element.Value;
        }
    }

    public IFlashObject<T>
        CreateFlashObject<T>(
            string path
        )
    {
        return new FlashObject<T>(
            Call<int>(
                "lnkCreate",
                path
            ),
            this
        );
    }

    private void FailPendingCalls(
        Exception exception
    )
    {
        foreach (
            KeyValuePair<
                long,
                TaskCompletionSource<
                    BridgeResult
                >
            > pending
            in _pendingCalls
        )
        {
            pending.Value
                .TrySetException(
                    exception
                );
        }

        _pendingCalls.Clear();
    }

    private void CleanupConnection()
    {
        try
        {
            _disposeCts?.Cancel();
        }
        catch
        {
            // ignored
        }

        try
        {
            if (
                _socket is not null &&
                (
                    _socket.State ==
                        WebSocketState.Open ||
                    _socket.State ==
                        WebSocketState.CloseReceived
                )
            )
            {
                _socket.CloseAsync(
                    WebSocketCloseStatus
                        .NormalClosure,
                    "Skua encerrado",
                    CancellationToken.None
                )
                .GetAwaiter()
                .GetResult();
            }
        }
        catch
        {
            // ignored
        }

        try
        {
            _socket?.Dispose();
        }
        catch
        {
            // ignored
        }

        _socket = null;

        try
        {
            _disposeCts?.Dispose();
        }
        catch
        {
            // ignored
        }

        _disposeCts = null;
        _receiveTask = null;
        _helloAcknowledged = null;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException
            .ThrowIf(
                _disposed,
                this
            );
    }

    private static void Log(
        string message
    )
    {
        try
        {
            string directory =
                Path.GetDirectoryName(
                    LogPath
                )!;

            Directory.CreateDirectory(
                directory
            );

            lock (LogLock)
            {
                File.AppendAllText(
                    LogPath,
                    $"{DateTime.Now:O} " +
                    $"{message}" +
                    Environment.NewLine
                );
            }
        }
        catch
        {
            // O diagnóstico não interrompe o cliente.
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        FailPendingCalls(
            new ObjectDisposedException(
                nameof(RuffleFlashUtil)
            )
        );

        CleanupConnection();

        _sendLock.Dispose();

        GC.SuppressFinalize(this);
    }

    private sealed record BridgeResult(
        bool Success,
        string? RawResult,
        string? Error
    );
}
