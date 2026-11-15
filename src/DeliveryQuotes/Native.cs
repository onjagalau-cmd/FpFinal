using System.Runtime.InteropServices;

namespace DeliveryQuotes;

// P/Invoke binding to native/dqnative.c  (int64_t dq_surcharge(int64_t, int32_t), cdecl).
// C# long = 64-bit, int = 32-bit, matching int64_t / int32_t on every platform. No native memory is allocated.
public static class NativeMethods
{
    public const string LibraryName = "dqnative";

    [DllImport(LibraryName, EntryPoint = "dq_surcharge", CallingConvention = CallingConvention.Cdecl)]
    public static extern long dq_surcharge(long priceUnits, int basisPoints);
}

// Adapter: validates domain input BEFORE the native call and turns loading/binding failures into Result failures.
public sealed class SurchargeCalculator
{
    public const long MaxPriceUnits = 922337203685477;
    public const int MaxBasisPoints = 10000;

    private readonly Func<long, int, long> _native;

    // Real native implementation (the library is loaded on the first call).
    public static SurchargeCalculator Native { get; } =
        new((priceUnits, basisPoints) => NativeMethods.dq_surcharge(priceUnits, basisPoints));

    // The native function is injectable so a failing library can be simulated in tests.
    public SurchargeCalculator(Func<long, int, long> native)
    {
        ArgumentNullException.ThrowIfNull(native);
        _native = native;
    }

    // price: money in whole currency units (rounded half away from zero); basisPoints: 500 = 5 %.
    public Result<long> Calculate(decimal price, int basisPoints)
    {
        if (price < 0)
            return Result<long>.Fail(new DomainError("INVALID_NATIVE_INPUT", $"Price must be >= 0 but was {price}."));
        if (basisPoints < 0 || basisPoints > MaxBasisPoints)
            return Result<long>.Fail(new DomainError("INVALID_NATIVE_INPUT",
                $"Basis points must be between 0 and {MaxBasisPoints} but was {basisPoints}."));

        var units = Math.Round(price, 0, MidpointRounding.AwayFromZero);
        if (units > MaxPriceUnits)
            return Result<long>.Fail(new DomainError("INVALID_NATIVE_INPUT", $"Price {price} is too large."));

        long surcharge;
        try
        {
            surcharge = _native((long)units, basisPoints);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            return Result<long>.Fail(new DomainError("NATIVE_UNAVAILABLE",
                $"Native library '{NativeMethods.LibraryName}' could not be loaded or bound ({ex.GetType().Name}): {ex.Message}"));
        }

        return surcharge < 0
            ? Result<long>.Fail(new DomainError("NATIVE_REJECTED_INPUT",
                $"Native library rejected price {units} / {basisPoints} bp."))
            : Result<long>.Ok(surcharge);
    }
}
