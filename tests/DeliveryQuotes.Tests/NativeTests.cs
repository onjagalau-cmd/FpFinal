using DeliveryQuotes;
using Xunit;

public class NativeTests
{
    private static long Value(Result<long> r) => Assert.IsType<Result<long>.Success>(r).Value;
    private static DomainError Error(Result<long> r) => Assert.IsType<Result<long>.Failure>(r).Error;

    // Real native call. Manual check: (700 * 500 + 5000) / 10000 = 355000 / 10000 = 35 (integer division).
    [Fact]
    public void RealNativeCall_ReturnsManuallyCalculatedSurcharge()
    {
        Assert.Equal(35L, Value(SurchargeCalculator.Native.Calculate(700m, 500)));
        Assert.Equal(38L, Value(SurchargeCalculator.Native.Calculate(1500m, 250)));   // 37.5 rounds half up
        Assert.Equal(0L, Value(SurchargeCalculator.Native.Calculate(0m, 500)));       // zero is valid
        Assert.Equal(1000L, Value(SurchargeCalculator.Native.Calculate(1000m, 10000)));   // 100 %
    }

    [Fact]
    public void RealNativeFunction_RejectsOutOfRangeArguments_WithMinusOne()
    {
        Assert.Equal(-1L, NativeMethods.dq_surcharge(-1, 500));
        Assert.Equal(-1L, NativeMethods.dq_surcharge(100, 10001));
        Assert.Equal(-1L, NativeMethods.dq_surcharge(100, -1));
        Assert.Equal(-1L, NativeMethods.dq_surcharge(SurchargeCalculator.MaxPriceUnits + 1, 1));
    }

    [Fact]
    public void Adapter_ValidatesInput_BeforeCallingNative()
    {
        var calls = 0;
        var adapter = new SurchargeCalculator((_, _) => { calls++; return 1; });

        Assert.Equal("INVALID_NATIVE_INPUT", Error(adapter.Calculate(-1m, 500)).Code);
        Assert.Equal("INVALID_NATIVE_INPUT", Error(adapter.Calculate(100m, 10001)).Code);
        Assert.Equal("INVALID_NATIVE_INPUT", Error(adapter.Calculate(100m, -1)).Code);
        Assert.Equal("INVALID_NATIVE_INPUT", Error(adapter.Calculate(SurchargeCalculator.MaxPriceUnits + 1m, 1)).Code);
        Assert.Equal(0, calls);
    }

    [Fact]
    public void Adapter_RoundsPriceToWholeUnits_BeforeCallingNative()
    {
        long seen = -1;
        var adapter = new SurchargeCalculator((units, _) => { seen = units; return 0; });

        adapter.Calculate(700.5m, 500);

        Assert.Equal(701L, seen);
    }

    [Fact]
    public void LoadFailure_BecomesClearResultFailure()
    {
        var adapter = new SurchargeCalculator((_, _) => throw new DllNotFoundException("dqnative missing"));

        var error = Error(adapter.Calculate(700m, 500));

        Assert.Equal("NATIVE_UNAVAILABLE", error.Code);
        Assert.Contains("dqnative", error.Message);
    }

    [Fact]
    public void NativeRejection_BecomesResultFailure()
    {
        var adapter = new SurchargeCalculator((_, _) => -1);

        Assert.Equal("NATIVE_REJECTED_INPUT", Error(adapter.Calculate(700m, 500)).Code);
    }
}
