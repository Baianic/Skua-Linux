using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace Skua.Backend.Linux.Bridge;

public sealed class RuffleBridgeClient : IAsyncDisposable
{
    private static readonly Uri BridgeUri =
        new("ws://127.0.0.1:8182");

    private static readonly TimeSpan ConnectTimeout =
        TimeSpan.FromSeconds(10);

    private static readonly TimeSpan CallTimeout =
        TimeSpan.FromSeconds(15);

    private static readonly TimeSpan CloseHandshakeTimeout =
        TimeSpan.FromSeconds(3);

    private static readonly bool FullCallLogging =
        Environment.GetEnvironmentVariable(
            "SKUA_BRIDGE_TRACE"
        ) is string traceValue &&
        (
            traceValue.Equals(
                "1",
                StringComparison.OrdinalIgnoreCase
            ) ||
            traceValue.Equals(
                "true",
                StringComparison.OrdinalIgnoreCase
            )
        );

    private static readonly string[] ImportantCallKeywords =
    {
        "accept",
        "attack",
        "bank",
        "buy",
        "drop",
        "equip",
        "join",
        "jump",
        "packet",
        "quest",
        "sell",
        "shop",
        "skill",
        "turnin"
    };

    private readonly ClientWebSocket _socket = new();

    private readonly string _connectionId =
        Guid.NewGuid().ToString("N")[..8];

    private readonly SemaphoreSlim _sendLock =
        new(1, 1);

    private readonly ConcurrentDictionary<
        long,
        TaskCompletionSource<JsonElement?>
    > _pendingCalls = new();

    private readonly Channel<BridgeEvent> _eventQueue =
        Channel.CreateUnbounded<BridgeEvent>(
            new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = true
            }
        );

    private readonly Channel<BridgeCommand> _commandQueue =
        Channel.CreateUnbounded<BridgeCommand>(
            new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = true
            }
        );

    private readonly CancellationTokenSource _shutdown =
        new();

    private TaskCompletionSource<bool> _helloAcknowledged =
        CreateCompletionSource<bool>();

    private Task? _receiveTask;
    private Task? _eventTask;
    private Task? _commandTask;

    private long _nextRequestId;
    private bool _disposed;

    public event EventHandler<BridgeEvent>? EventReceived;

    public Func<
        BridgeCommand,
        CancellationToken,
        Task<BridgeCommandResult>
    >? CommandHandler { get; set; }

    public bool IsConnected =>
        _socket.State == WebSocketState.Open;

    private bool CanReceive =>
        _socket.State is
            WebSocketState.Open or
            WebSocketState.CloseSent;

    public async Task ConnectAsync(
        CancellationToken cancellationToken = default
    )
    {
        ThrowIfDisposed();

        if (IsConnected)
        {
            return;
        }

        using CancellationTokenSource timeout =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _shutdown.Token
            );

        timeout.CancelAfter(ConnectTimeout);

        LogLifecycle(
            $"ConnectAsync início; uri={BridgeUri}"
        );

        await _socket.ConnectAsync(
            BridgeUri,
            timeout.Token
        );

        LogLifecycle("WebSocket conectado.");
        Console.WriteLine(
            FullCallLogging
                ? "Log da ponte: completo."
                : "Log da ponte: seletivo."
        );

        _receiveTask = Task.Run(
            () => ReceiveLoopAsync(_shutdown.Token),
            CancellationToken.None
        );

        _eventTask = Task.Run(
            () => EventLoopAsync(_shutdown.Token),
            CancellationToken.None
        );

        _commandTask = Task.Run(
            () => CommandLoopAsync(_shutdown.Token),
            CancellationToken.None
        );

        await SendMessageAsync(
            new
            {
                type = "hello",
                role = "host"
            },
            timeout.Token
        );

        await _helloAcknowledged.Task.WaitAsync(
            ConnectTimeout,
            cancellationToken
        );

        Console.WriteLine(
            "Backend registrado como host."
        );
    }

    public async Task<JsonElement?> CallAsync(
        string function,
        params object?[] args
    )
    {
        ThrowIfDisposed();

        if (!IsConnected)
        {
            throw new InvalidOperationException(
                "O backend não está conectado à ponte."
            );
        }

        long id = Interlocked.Increment(
            ref _nextRequestId
        );

        TaskCompletionSource<JsonElement?> completion =
            CreateCompletionSource<JsonElement?>();

        if (!_pendingCalls.TryAdd(id, completion))
        {
            throw new InvalidOperationException(
                $"Não foi possível registrar a chamada {id}."
            );
        }

        bool logCall = ShouldLogCall(function);

        try
        {
            if (logCall)
            {
                Console.WriteLine(
                    $"C# -> Ruffle: #{id} {function}"
                );
            }

            await SendMessageAsync(
                new
                {
                    type = "call",
                    id,
                    function,
                    args
                },
                _shutdown.Token
            );

            JsonElement? result =
                await completion.Task.WaitAsync(
                    CallTimeout,
                    _shutdown.Token
                );

            if (logCall)
            {
                Console.WriteLine(
                    $"Ruffle -> C#: #{id} {function} = " +
                    FormatResult(result)
                );
            }

            return result;
        }
        catch (OperationCanceledException)
            when (_shutdown.IsCancellationRequested)
        {
            /*
             * Cancelamento esperado durante o
             * encerramento normal da ponte.
             */
            throw;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(
                $"Falha na chamada da ponte " +
                $"#{id} '{function}':"
            );

            Console.Error.WriteLine(exception);
            throw;
        }
        finally
        {
            _pendingCalls.TryRemove(id, out _);
        }
    }

    public async Task<T?> CallAsync<T>(
        string function,
        params object?[] args
    )
    {
        JsonElement? result =
        await CallAsync(function, args);

        if (
            result is null ||
            result.Value.ValueKind is
            JsonValueKind.Null or
            JsonValueKind.Undefined
        )
        {
            return default;
        }

        JsonElement element = result.Value;
        Type targetType =
        Nullable.GetUnderlyingType(typeof(T)) ??
        typeof(T);

        if (targetType == typeof(string))
        {
            string? value =
            element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : element.GetRawText();

            if (
                value is not null &&
                value.Length >= 2 &&
                value.StartsWith('"') &&
                value.EndsWith('"')
            )
            {
                try
                {
                    value =
                    JsonSerializer.Deserialize<string>(
                        value
                    );
                }
                catch (JsonException)
                {
                    // Mantém o texto original caso não seja
                    // uma string JSON válida.
                }
            }

            return (T?)(object?)value;
        }

        if (
            targetType == typeof(bool) &&
            element.ValueKind == JsonValueKind.String
        )
        {
            string? text = element.GetString();

            if (bool.TryParse(text, out bool boolean))
            {
                return (T?)(object)boolean;
            }
        }

        if (
            targetType == typeof(int) &&
            element.ValueKind == JsonValueKind.String
        )
        {
            string? text = element.GetString();

            if (int.TryParse(text, out int integer))
            {
                return (T?)(object)integer;
            }
        }

        if (
            targetType == typeof(long) &&
            element.ValueKind == JsonValueKind.String
        )
        {
            string? text = element.GetString();

            if (long.TryParse(text, out long integer))
            {
                return (T?)(object)integer;
            }
        }

        if (
            targetType == typeof(double) &&
            element.ValueKind == JsonValueKind.String
        )
        {
            string? text = element.GetString();

            if (
                double.TryParse(
                    text,
                    System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out double number
                )
            )
            {
                return (T?)(object)number;
            }
        }

        return element.Deserialize<T>();
    }

    private async Task SendMessageAsync(
        object message,
        CancellationToken cancellationToken
    )
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(
            message
        );

        await _sendLock.WaitAsync(cancellationToken);

        try
        {
            if (!IsConnected)
            {
                throw new WebSocketException(
                    "A ponte WebSocket não está aberta."
                );
            }

            await _socket.SendAsync(
                bytes,
                WebSocketMessageType.Text,
                endOfMessage: true,
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
        byte[] buffer = new byte[16 * 1024];

        LogLifecycle("ReceiveLoop iniciado.");

        try
        {
            while (
                !cancellationToken.IsCancellationRequested &&
                CanReceive
            )
            {
                using MemoryStream messageStream = new();

                WebSocketReceiveResult result;

                do
                {
                    result = await _socket.ReceiveAsync(
                        buffer,
                        cancellationToken
                    );

                    if (
                        result.MessageType ==
                        WebSocketMessageType.Close
                    )
                    {
                        LogLifecycle(
                            "Close frame recebido; " +
                            $"resultCloseStatus={result.CloseStatus?.ToString() ?? "<null>"}; " +
                            $"resultCloseDescription={FormatLogValue(result.CloseStatusDescription)}"
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

                string json = Encoding.UTF8.GetString(
                    messageStream.ToArray()
                );

                ProcessIncomingMessage(json);
            }
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            LogLifecycle(
                "ReceiveLoop cancelado pelo shutdown."
            );
        }
        catch (Exception exception)
        {
            LogLifecycleException(
                "ReceiveLoop falhou",
                exception
            );

            FailPendingCalls(exception);
        }
        finally
        {
            LogLifecycle("ReceiveLoop finalizado.");
            _eventQueue.Writer.TryComplete();
            _commandQueue.Writer.TryComplete();
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

            switch (typeElement.GetString())
            {
                case "hello-ack":
                    _helloAcknowledged.TrySetResult(
                        true
                    );
                    break;

                case "result":
                    ProcessResult(root);
                    break;

                case "event":
                    ProcessEvent(root);
                    break;

                case "command":
                    ProcessCommand(root);
                    break;

                case "error":
                    Console.Error.WriteLine(
                        $"Erro da ponte: {json}"
                    );
                    break;

                default:
                    Console.WriteLine(
                        $"Mensagem desconhecida: {json}"
                    );
                    break;
            }
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(
                $"Mensagem inválida: {json}"
            );

            Console.Error.WriteLine(exception);
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
            !idElement.TryGetInt64(out long id)
        )
        {
            return;
        }

        if (
            !_pendingCalls.TryGetValue(
                id,
                out TaskCompletionSource<
                    JsonElement?
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

        if (!success)
        {
            string error =
                root.TryGetProperty(
                    "error",
                    out JsonElement errorElement
                )
                    ? errorElement.ToString()
                    : "A chamada falhou.";

            completion.TrySetException(
                new InvalidOperationException(error)
            );

            return;
        }

        JsonElement? result = null;

        if (
            root.TryGetProperty(
                "result",
                out JsonElement resultElement
            ) &&
            resultElement.ValueKind is not
                JsonValueKind.Null and not
                JsonValueKind.Undefined
        )
        {
            result = resultElement.Clone();
        }

        completion.TrySetResult(result);
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

        string? name = nameElement.GetString();

        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        JsonElement[] args =
            Array.Empty<JsonElement>();

        if (
            root.TryGetProperty(
                "args",
                out JsonElement argsElement
            ) &&
            argsElement.ValueKind ==
                JsonValueKind.Array
        )
        {
            args = argsElement
                .EnumerateArray()
                .Select(element => element.Clone())
                .ToArray();
        }

        _eventQueue.Writer.TryWrite(
            new BridgeEvent(name, args)
        );
    }


    private void ProcessCommand(
        JsonElement root
    )
    {
        if (
            !root.TryGetProperty(
                "id",
                out JsonElement idElement
            ) ||
            !idElement.TryGetInt64(out long id) ||
            !root.TryGetProperty(
                "name",
                out JsonElement nameElement
            )
        )
        {
            return;
        }

        string? name = nameElement.GetString();

        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        JsonElement[] args =
            Array.Empty<JsonElement>();

        if (
            root.TryGetProperty(
                "args",
                out JsonElement argsElement
            ) &&
            argsElement.ValueKind ==
                JsonValueKind.Array
        )
        {
            args = argsElement
                .EnumerateArray()
                .Select(element => element.Clone())
                .ToArray();
        }

        _commandQueue.Writer.TryWrite(
            new BridgeCommand(
                id,
                name,
                args
            )
        );
    }

    private async Task CommandLoopAsync(
        CancellationToken cancellationToken
    )
    {
        try
        {
            await foreach (
                BridgeCommand command in
                _commandQueue.Reader.ReadAllAsync(
                    cancellationToken
                )
            )
            {
                BridgeCommandResult result;

                try
                {
                    Func<
                        BridgeCommand,
                        CancellationToken,
                        Task<BridgeCommandResult>
                    >? handler = CommandHandler;

                    result = handler is null
                        ? BridgeCommandResult.Failure(
                            "command-handler-not-configured"
                        )
                        : await handler(
                            command,
                            cancellationToken
                        );
                }
                catch (OperationCanceledException)
                    when (
                        cancellationToken
                            .IsCancellationRequested
                    )
                {
                    break;
                }
                catch (Exception exception)
                {
                    Console.Error.WriteLine(
                        $"Erro no comando " +
                        $"'{command.Name}':"
                    );

                    Console.Error.WriteLine(
                        exception
                    );

                    result =
                        BridgeCommandResult.Failure(
                            exception.Message
                        );
                }

                try
                {
                    await SendMessageAsync(
                        new
                        {
                            type = "command-result",
                            id = command.Id,
                            success = result.Success,
                            result = result.Result,
                            error = result.Error
                        },
                        cancellationToken
                    );
                }
                catch (OperationCanceledException)
                    when (
                        cancellationToken
                            .IsCancellationRequested
                    )
                {
                    break;
                }
                catch (Exception exception)
                {
                    Console.Error.WriteLine(
                        $"Falha ao responder comando " +
                        $"'{command.Name}':"
                    );

                    Console.Error.WriteLine(
                        exception
                    );
                }
            }
        }
        catch (OperationCanceledException)
            when (
                cancellationToken
                    .IsCancellationRequested
            )
        {
            // Encerramento normal.
        }
    }

    private async Task EventLoopAsync(
        CancellationToken cancellationToken
    )
    {
        try
        {
            await foreach (
                BridgeEvent bridgeEvent in
                _eventQueue.Reader.ReadAllAsync(
                    cancellationToken
                )
            )
            {
                try
                {
                    EventReceived?.Invoke(
                        this,
                        bridgeEvent
                    );
                }
                catch (Exception exception)
                {
                    Console.Error.WriteLine(
                        $"Erro no evento " +
                        $"'{bridgeEvent.Name}':"
                    );

                    Console.Error.WriteLine(
                        exception
                    );
                }
            }
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            // Encerramento normal.
        }
    }

    private void FailPendingCalls(
        Exception exception
    )
    {
        foreach (
            KeyValuePair<
                long,
                TaskCompletionSource<JsonElement?>
            > pendingCall in _pendingCalls
        )
        {
            pendingCall.Value.TrySetException(
                exception
            );
        }

        _pendingCalls.Clear();
    }

    private static bool ShouldLogCall(
        string function
    )
    {
        if (FullCallLogging)
        {
            return true;
        }

        foreach (
            string keyword in
            ImportantCallKeywords
        )
        {
            if (
                function.Contains(
                    keyword,
                    StringComparison.OrdinalIgnoreCase
                )
            )
            {
                return true;
            }
        }

        return false;
    }

    private static string FormatResult(
        JsonElement? result
    )
    {
        const int maxLoggedResultLength = 500;

        if (result is null)
        {
            return "<null>";
        }

        string rawResult =
        result.Value.ValueKind ==
        JsonValueKind.String
        ? result.Value.GetString() ??
        "<null>"
        : result.Value.GetRawText();

        /*
         * Valores pequenos continuam sendo exibidos
         * integralmente para facilitar o diagnóstico.
         */
        if (
            rawResult.Length <=
            maxLoggedResultLength
        )
        {
            return rawResult;
        }

        /*
         * Algumas respostas chegam como um JsonElement
         * real, enquanto outras chegam como uma string
         * que contém JSON. JsonDocument.Parse atende
         * aos dois casos depois da extração acima.
         */
        try
        {
            using JsonDocument document =
            JsonDocument.Parse(rawResult);

            JsonElement root =
            document.RootElement;

            return root.ValueKind switch
            {
                JsonValueKind.Array =>
                $"<JSON array: " +
                $"{root.GetArrayLength()} itens, " +
                $"{rawResult.Length} caracteres>",

                JsonValueKind.Object =>
                $"<JSON object: " +
                $"{rawResult.Length} caracteres>",

                JsonValueKind.String =>
                $"<texto JSON: " +
                $"{rawResult.Length} caracteres>",

                _ =>
                $"<JSON {root.ValueKind}: " +
                $"{rawResult.Length} caracteres>"
            };
        }
        catch (JsonException)
        {
            /*
             * Respostas extensas que não forem JSON
             * também não devem ocupar todo o log.
             */
            return
            $"<resultado longo: " +
            $"{rawResult.Length} caracteres>";
        }
    }

    private void LogLifecycle(
        string message
    )
    {
        Console.WriteLine(
            $"[bridge-life {DateTimeOffset.Now:HH:mm:ss.fff zzz}] " +
            $"conn={_connectionId} " +
            $"state={_socket.State} " +
            $"closeStatus={_socket.CloseStatus?.ToString() ?? "<null>"} " +
            $"closeDescription={FormatLogValue(_socket.CloseStatusDescription)} " +
            $"shutdown={_shutdown.IsCancellationRequested} :: " +
            message
        );
    }

    private void LogLifecycleException(
        string message,
        Exception exception
    )
    {
        LogLifecycle(
            $"{message}; " +
            $"exception={exception.GetType().FullName}; " +
            $"message={FormatLogValue(exception.Message)}"
        );

        if (exception is WebSocketException webSocketException)
        {
            Console.Error.WriteLine(
                $"[bridge-life] conn={_connectionId} " +
                $"WebSocketErrorCode={webSocketException.WebSocketErrorCode}"
            );
        }

        Console.Error.WriteLine(exception);
    }

    private static string FormatLogValue(
        string? value
    )
    {
        if (string.IsNullOrEmpty(value))
        {
            return "<empty>";
        }

        return JsonSerializer.Serialize(value);
    }

    private static TaskCompletionSource<T>
        CreateCompletionSource<T>()
    {
        return new TaskCompletionSource<T>(
            TaskCreationOptions
                .RunContinuationsAsynchronously
        );
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(
            _disposed,
            this
        );
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        LogLifecycle("DisposeAsync iniciado.");

        FailPendingCalls(
            new OperationCanceledException(
                "A ponte foi encerrada."
            )
        );

        if (
            _socket.State is
                WebSocketState.Open or
                WebSocketState.CloseReceived
        )
        {
            try
            {
                await _sendLock.WaitAsync(
                    CancellationToken.None
                );

                try
                {
                    LogLifecycle(
                        "DisposeAsync enviando CloseOutputAsync normal."
                    );

                    await _socket.CloseOutputAsync(
                        WebSocketCloseStatus
                            .NormalClosure,
                        "Backend encerrado",
                        CancellationToken.None
                    );

                    LogLifecycle(
                        "DisposeAsync CloseOutputAsync concluído."
                    );
                }
                finally
                {
                    _sendLock.Release();
                }
            }
            catch (Exception exception)
            {
                LogLifecycleException(
                    "DisposeAsync CloseOutputAsync falhou",
                    exception
                );
            }
        }

        if (_receiveTask is not null)
        {
            try
            {
                LogLifecycle(
                    "DisposeAsync aguardando close handshake remoto."
                );

                await _receiveTask.WaitAsync(
                    CloseHandshakeTimeout
                );

                LogLifecycle(
                    "DisposeAsync receive loop encerrou durante o handshake."
                );
            }
            catch (TimeoutException)
            {
                LogLifecycle(
                    "DisposeAsync timeout aguardando close handshake; " +
                    "cancelando receive loop como fallback."
                );

                _shutdown.Cancel();

                try
                {
                    await _receiveTask;
                }
                catch
                {
                    // Erro já tratado pelo loop.
                }
            }
            catch
            {
                // Erro já tratado pelo loop.
            }
        }

        if (!_shutdown.IsCancellationRequested)
        {
            LogLifecycle(
                "DisposeAsync cancelando serviços auxiliares após o handshake."
            );

            _shutdown.Cancel();
        }

        if (_eventTask is not null)
        {
            try
            {
                await _eventTask;
            }
            catch
            {
                // Erro já tratado pelo loop.
            }
        }

        if (_commandTask is not null)
        {
            try
            {
                await _commandTask;
            }
            catch
            {
                // Erro já tratado pelo loop.
            }
        }

        LogLifecycle(
            "DisposeAsync antes de Dispose do socket."
        );

        _socket.Dispose();
        _sendLock.Dispose();
        _shutdown.Dispose();
    }

}

public sealed record BridgeEvent(
    string Name,
    IReadOnlyList<JsonElement> Arguments
);

public sealed record BridgeCommand(
    long Id,
    string Name,
    IReadOnlyList<JsonElement> Arguments
);

public sealed record BridgeCommandResult(
    bool Success,
    object? Result,
    string? Error
)
{
    public static BridgeCommandResult Ok(
        object? result = null
    ) =>
        new(
            true,
            result,
            null
        );

    public static BridgeCommandResult Failure(
        string error
    ) =>
        new(
            false,
            null,
            error
        );
}
