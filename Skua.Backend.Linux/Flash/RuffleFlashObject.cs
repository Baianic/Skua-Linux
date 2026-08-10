using Newtonsoft.Json;
using Skua.Core.Interfaces;

namespace Skua.Backend.Linux.Flash;

public class RuffleFlashObject<T> : IFlashObject<T>
{
    private bool _disposed;

    public RuffleFlashObject(
        int id,
        IFlashUtil flashUtil
    )
    {
        ID = id;
        FlashUtil = flashUtil;
    }

    public IFlashUtil FlashUtil { get; init; }

    public int ID { get; }

    public T? Value
    {
        get
        {
            ThrowIfDisposed();

            try
            {
                string? json = FlashUtil.Call(
                    "lnkGetValue",
                    ID
                );

                return json is null
                ? default
                : JsonConvert.DeserializeObject<T>(
                    json
                );
            }
            catch
            {
                return default;
            }
        }

        set
        {
            ThrowIfDisposed();

            FlashUtil.Call(
                "lnkSetValue",
                ID,
                value!
            );
        }
    }

    public IFlashObject<TResult> GetChild<TResult>(
        string path
    )
    {
        ThrowIfDisposed();

        int childId = FlashUtil.Call<int>(
            "lnkGetChild",
            ID,
            path
        );

        return new RuffleFlashObject<TResult>(
            childId,
            FlashUtil
        );
    }

    public void ClearChild(string path)
    {
        ThrowIfDisposed();

        FlashUtil.Call(
            "lnkDeleteChild",
            ID,
            path
        );
    }

    public IFlashArray<T> ToArray()
    {
        ThrowIfDisposed();

        return new RuffleFlashArray<T>(
            ID,
            FlashUtil
        );
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        FlashUtil.Call(
            "lnkDestroy",
            ID
        );

        GC.SuppressFinalize(this);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(
            _disposed,
            this
        );
    }
}
