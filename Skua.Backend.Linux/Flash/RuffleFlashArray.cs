using Skua.Core.Interfaces;

namespace Skua.Backend.Linux.Flash;

public sealed class RuffleFlashArray<T> :
    RuffleFlashObject<T[]>,
    IFlashArray<T>
{
    public RuffleFlashArray(
        int id,
        IFlashUtil flashUtil
    ) : base(id, flashUtil)
    {
    }

    public IFlashObject<T> Get(int index)
    {
        int childId = FlashUtil.Call<int>(
            "lnkGetArray",
            ID,
            index
        );

        return new RuffleFlashObject<T>(
            childId,
            FlashUtil
        );
    }

    public void Set(
        int index,
        T value
    )
    {
        FlashUtil.Call(
            "lnkSetArray",
            ID,
            index,
            value!
        );
    }
}
