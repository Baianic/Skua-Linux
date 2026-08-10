using Newtonsoft.Json;
using Skua.Backend.Linux.Bridge;
using Skua.Core.Interfaces;
using System.Dynamic;
using System.Globalization;
using System.Text.Json;
using System.Xml.Linq;

namespace Skua.Backend.Linux.Flash;

public sealed class RuffleFlashUtil : IFlashUtil
{
    private readonly RuffleBridgeClient _bridge;
    private readonly object _initializationLock = new();

    private bool _initialized;
    private bool _disposed;

    public RuffleFlashUtil(
        RuffleBridgeClient? bridge = null
    )
    {
        _bridge =
            bridge ??
            new RuffleBridgeClient();

        _bridge.EventReceived += HandleBridgeEvent;
    }

    public event FlashCallHandler? FlashCall;

    public bool IsConnected =>
        _bridge.IsConnected;

    public void InitializeFlash()
    {
        ThrowIfDisposed();

        lock (_initializationLock)
        {
            if (_initialized && _bridge.IsConnected)
            {
                return;
            }

            _bridge
                .ConnectAsync()
                .GetAwaiter()
                .GetResult();

            _initialized = true;
        }
    }

    public string? Call(
        string function,
        params object[] args
    )
    {
        ThrowIfDisposed();
        EnsureInitialized();

        JsonElement? result = _bridge
            .CallAsync(
                function,
                args.Cast<object?>().ToArray()
            )
            .GetAwaiter()
            .GetResult();

        return ConvertResultToJsonText(result);
    }

    public T? Call<T>(
        string function,
        params object[] args
    )
    {
        ThrowIfDisposed();
        EnsureInitialized();

        try
        {
            return _bridge
                .CallAsync<T>(
                    function,
                    args.Cast<object?>().ToArray()
                )
                .GetAwaiter()
                .GetResult();
        }
        catch
        {
            return default;
        }
    }

    public object? Call(
        string function,
        Type type,
        params object[] args
    )
    {
        ThrowIfDisposed();
        EnsureInitialized();

        try
        {
            JsonElement? result = _bridge
                .CallAsync(
                    function,
                    args.Cast<object?>().ToArray()
                )
                .GetAwaiter()
                .GetResult();

            return ConvertResult(
                result,
                type
            );
        }
        catch
        {
            return type.IsValueType
                ? Activator.CreateInstance(type)
                : null;
        }
    }

    public object FromFlashXml(XElement element)
    {
        return element.Name.LocalName switch
        {
            "number" => ParseXmlNumber(
                element.Value
            ),

            "true" => true,
            "false" => false,
            "null" => null!,

            "array" => element
                .Elements()
                .Select(FromFlashXml)
                .ToArray(),

            "object" => ConvertXmlObject(
                element
            ),

            _ => element.Value
        };
    }

    public IFlashObject<T> CreateFlashObject<T>(
        string path
    )
    {
        int id = Call<int>(
            "lnkCreate",
            path
        );

        return new RuffleFlashObject<T>(
            id,
            this
        );
    }

    private void HandleBridgeEvent(
        object? sender,
        BridgeEvent bridgeEvent
    )
    {
        FlashCallHandler? handlers =
            FlashCall;

        if (handlers is null)
        {
            return;
        }

        object[] args = bridgeEvent
            .Arguments
            .Select(ConvertJsonElement)
            .ToArray();

        try
        {
            handlers.Invoke(
                bridgeEvent.Name,
                args
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

    private static object ConvertJsonElement(
        JsonElement element
    )
    {
        return element.ValueKind switch
        {
            JsonValueKind.String =>
                element.GetString() ??
                string.Empty,

            JsonValueKind.Number =>
                element.TryGetInt32(
                    out int integer
                )
                    ? integer
                    : element.TryGetInt64(
                        out long longInteger
                    )
                        ? longInteger
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

    private static string? ConvertResultToJsonText(
        JsonElement? result
    )
    {
        if (
            result is null ||
            result.Value.ValueKind is
                JsonValueKind.Null or
                JsonValueKind.Undefined
        )
        {
            return null;
        }

        JsonElement element = result.Value;

        /*
         * A ponte frequentemente devolve resultados
         * serializados dentro de uma string.
         *
         * Exemplo:
         *   JsonElement String contendo: "AtaNowak"
         *
         * Para GetGameObject, precisamos preservar:
         *   "AtaNowak"
         *
         * porque a interface posteriormente utiliza
         * JsonConvert.DeserializeObject<T>.
         */
        if (
            element.ValueKind ==
            JsonValueKind.String
        )
        {
            return element.GetString();
        }

        return element.GetRawText();
    }

    private static object? ConvertResult(
        JsonElement? result,
        Type targetType
    )
    {
        Type effectiveType =
            Nullable.GetUnderlyingType(
                targetType
            ) ?? targetType;

        if (
            result is null ||
            result.Value.ValueKind is
                JsonValueKind.Null or
                JsonValueKind.Undefined
        )
        {
            return targetType.IsValueType
                ? Activator.CreateInstance(
                    targetType
                )
                : null;
        }

        JsonElement element = result.Value;

        if (effectiveType == typeof(string))
        {
            return element.ValueKind ==
                JsonValueKind.String
                    ? element.GetString()
                    : element.GetRawText();
        }

        if (
            element.ValueKind ==
            JsonValueKind.String
        )
        {
            string? text =
                element.GetString();

            if (
                effectiveType == typeof(bool) &&
                bool.TryParse(
                    text,
                    out bool boolean
                )
            )
            {
                return boolean;
            }

            if (
                effectiveType.IsEnum &&
                Enum.TryParse(
                    effectiveType,
                    text,
                    ignoreCase: true,
                    out object? enumValue
                )
            )
            {
                return enumValue;
            }

            try
            {
                return Convert.ChangeType(
                    text,
                    effectiveType,
                    CultureInfo.InvariantCulture
                );
            }
            catch
            {
                if (
                    !string.IsNullOrWhiteSpace(
                        text
                    )
                )
                {
                    try
                    {
                        return JsonConvert
                            .DeserializeObject(
                                text,
                                effectiveType
                            );
                    }
                    catch
                    {
                        // Retorno padrão abaixo.
                    }
                }
            }
        }

        try
        {
            return JsonConvert.DeserializeObject(
                element.GetRawText(),
                effectiveType
            );
        }
        catch
        {
            return targetType.IsValueType
                ? Activator.CreateInstance(
                    targetType
                )
                : null;
        }
    }

    private static object ParseXmlNumber(
        string value
    )
    {
        if (
            int.TryParse(
                value,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out int integer
            )
        )
        {
            return integer;
        }

        if (
            double.TryParse(
                value,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out double number
            )
        )
        {
            return number;
        }

        return 0;
    }

    private object ConvertXmlObject(
        XElement element
    )
    {
        IDictionary<string, object?> result =
            new ExpandoObject();

        foreach (
            XElement property in
            element.Elements()
        )
        {
            string? id = property
                .Attribute("id")
                ?.Value;

            XElement? valueElement =
                property.Elements()
                    .FirstOrDefault();

            if (
                string.IsNullOrWhiteSpace(id) ||
                valueElement is null
            )
            {
                continue;
            }

            result[id] = FromFlashXml(
                valueElement
            );
        }

        return (ExpandoObject)result;
    }

    private void EnsureInitialized()
    {
        if (
            !_initialized ||
            !_bridge.IsConnected
        )
        {
            InitializeFlash();
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(
            _disposed,
            this
        );
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _bridge.EventReceived -=
            HandleBridgeEvent;

        _bridge
            .DisposeAsync()
            .AsTask()
            .GetAwaiter()
            .GetResult();

        GC.SuppressFinalize(this);
    }
}
