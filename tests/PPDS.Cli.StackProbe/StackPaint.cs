using System.Runtime.CompilerServices;

namespace PPDS.Cli.StackProbe;

/// <summary>
/// Measures the stack a call uses without risking an overflow: fill a region below the current frame with a
/// mark, run the work, then find the deepest byte it overwrote. Agrees with a crash search to within a few
/// bytes per level. Callers keep garbage collection out of the measured call (the runtime can write deep into
/// a large free stack during a collection, which would read as use).
/// </summary>
internal static unsafe class StackPaint
{
    private const byte Mark = 0xCD;

    /// <summary>Bytes of stack <paramref name="work"/> used below the caller's frame, within <paramref name="region"/> bytes.</summary>
    public static long Measure(Action work, int region)
    {
        Fill(region);
        work();
        return Scan(region);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    [SkipLocalsInit]
    private static void Fill(int size)
    {
        byte* p = stackalloc byte[size];
        for (var i = 0; i < size; i++)
            p[i] = Mark;
        Keep(p);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    [SkipLocalsInit]
    private static long Scan(int size)
    {
        byte* p = stackalloc byte[size];
        // The two regions can start a few hundred bytes apart: skip the bottom 4 KB, then any unpainted
        // bytes; the run of bytes that still hold the mark was never reached by the work.
        var i = 4096;
        while (i < size && p[i] != Mark) i++;
        while (i < size && p[i] == Mark) i++;
        Keep(p);
        return size - i;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Keep(byte* p) { }
}
