using System.Runtime;

namespace Mithu.App.Services;

/// <summary>
/// Frees the big, short-lived copies a screenshot leaves behind (the frozen screen, the picker's image, PNG and
/// base64 copies). .NET keeps large objects until a full collection, which can take a long while to come, so after
/// these rare moments Mithu runs one full collection that also compacts the large-object heap. This releases
/// memory that is no longer used (it isn't the cosmetic working-set trim: nothing is paged out).
/// </summary>
public static class MemoryRelief
{
    public static void AfterBigImages()
    {
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers(); // bitmaps hold native pixel memory until their finalizers run
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
    }
}
