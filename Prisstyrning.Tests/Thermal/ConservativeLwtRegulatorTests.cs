using Prisstyrning.Thermal.Control;

namespace Prisstyrning.Tests.Thermal;

public class ConservativeLwtRegulatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);
    private readonly ConservativeLwtRegulator _regulator = new();

    [Theory]
    [InlineData(.5, .5)]
    [InlineData(-.5, -.5)]
    [InlineData(9, .5)]
    [InlineData(double.NaN, 0)]
    public void ValidatedBias_IsBoundedAndInvalidBiasCannotDisableBasicControl(double bias, double expected)
    {
        var result = _regulator.EvaluateWithValidatedBias(Input(), bias);
        Assert.Equal(expected, result.RequestedDeviationC);
        Assert.False(result.IsFallback);
    }

    [Fact]
    public void ValidatedNegativeBias_CannotSacrificeACriticalRoom()
    {
        var result = _regulator.EvaluateWithValidatedBias(Input() with { CriticalRoomBelowMinimum = true }, -.5);
        Assert.Equal(.5, result.RequestedDeviationC);
    }

    [Fact]
    public void ColdRoom_UsesOnlyFeedbackAndOneStepWithoutAnyModelOrPlan()
    {
        var decision = _regulator.Evaluate(Input() with { RepresentativeTemperatureErrorC = -1 });
        Assert.True(decision.ShouldWrite);
        Assert.False(decision.IsFallback);
        Assert.Equal(.5, decision.RequestedDeviationC);
        Assert.Contains("inget pris- eller modellbidrag", decision.Reason);
    }

    [Theory]
    [InlineData(-.1)]
    [InlineData(0)]
    [InlineData(.1)]
    public void StableRoom_InDeadbandDoesNotAccumulateCorrection(double error)
    {
        var decision = _regulator.Evaluate(Input() with { RepresentativeTemperatureErrorC = error });
        Assert.False(decision.ShouldWrite);
        Assert.Equal(0, decision.NewIntegral);
    }

    [Fact]
    public void SixEvaluations_IntegrateHalfAnHourNotTimeSinceLastWrite()
    {
        var integral = 0d;
        for (var minute = 5; minute <= 30; minute += 5)
        {
            var decision = _regulator.Evaluate(Input() with
            {
                NowUtc = Now.AddMinutes(minute), TelemetryUtc = Now.AddMinutes(minute),
                PreviousEvaluationUtc = Now.AddMinutes(minute - 5),
                LastAcceptedWriteUtc = Now.AddHours(-4),
                RepresentativeTemperatureErrorC = -.2, Integral = integral
            });
            integral = decision.NewIntegral;
        }
        Assert.Equal(.1, integral, 10);
    }

    [Fact]
    public void DuplicateEvaluation_DoesNotIntegrateAgain()
    {
        var result = _regulator.Evaluate(Input() with
        {
            PreviousEvaluationUtc = Now, Integral = .5, RepresentativeTemperatureErrorC = -.2
        });
        Assert.Equal(.5, result.NewIntegral);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(11)]
    [InlineData(180)]
    public void RestartOrLongGap_DiscardsOldIntegralAndUsesProportionalOnly(int? minutes)
    {
        var result = _regulator.Evaluate(Input() with
        {
            PreviousEvaluationUtc = minutes is { } value ? Now.AddMinutes(-value) : null,
            Integral = 8, RepresentativeTemperatureErrorC = -.2
        });
        Assert.Equal(0, result.NewIntegral);
        Assert.Equal(0, result.RequestedDeviationC);
        Assert.False(result.IsFallback);
    }

    [Theory]
    [InlineData(-2, 9)]
    [InlineData(2, -9)]
    public void SaturatedOutput_DoesNotAccumulateFurther(double error, double integral)
    {
        var result = _regulator.Evaluate(Input() with
        {
            RepresentativeTemperatureErrorC = error, Integral = integral
        });
        Assert.Equal(integral, result.NewIntegral);
        Assert.InRange(result.RequestedDeviationC, -.5, .5);
    }

    [Fact]
    public void ErrorReverses_IntegralCanUnwind()
    {
        var result = _regulator.Evaluate(Input() with { RepresentativeTemperatureErrorC = .2, Integral = 9 });
        Assert.True(result.NewIntegral < 9);
    }

    [Theory]
    [InlineData(.5)]
    [InlineData(1)]
    public void SustainedCold_RespectsHardwareStepAndOneDegreeLimit(double step)
    {
        var input = Input() with { RepresentativeTemperatureErrorC = -2, DeviationStepC = step };
        var first = _regulator.Evaluate(input);
        Assert.Equal(step, first.RequestedDeviationC);
        var second = _regulator.Evaluate(input with
        {
            NowUtc = Now.AddMinutes(30), TelemetryUtc = Now.AddMinutes(30),
            PreviousEvaluationUtc = Now.AddMinutes(25), LastAcceptedWriteUtc = Now,
            ObservedDeviationC = first.RequestedDeviationC, Integral = first.NewIntegral
        });
        Assert.Equal(1, second.RequestedDeviationC);
    }

    [Fact]
    public void ReverseDirection_ChangesOnlyOneStep()
    {
        var result = _regulator.Evaluate(Input() with { ObservedDeviationC = 1, RepresentativeTemperatureErrorC = 2 });
        Assert.Equal(.5, result.RequestedDeviationC);
    }

    [Theory]
    [InlineData(29, false)]
    [InlineData(30, true)]
    public void NormalWrites_RespectThirtyMinuteLimit(int minutes, bool expected)
    {
        var result = _regulator.Evaluate(Input() with
        {
            LastAcceptedWriteUtc = Now.AddMinutes(-minutes), RepresentativeTemperatureErrorC = -1
        });
        Assert.Equal(expected, result.ShouldWrite);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void DhwOrDefrost_FreezesOutputAndIntegral(bool dhw, bool defrost)
    {
        var result = _regulator.Evaluate(Input() with
        {
            DhwActive = dhw, DefrostActive = defrost, ObservedDeviationC = .5,
            Integral = 2, RepresentativeTemperatureErrorC = -1, FlowLitresPerMinute = 0
        });
        Assert.False(result.ShouldWrite);
        Assert.False(result.IsFallback);
        Assert.Equal(.5, result.RequestedDeviationC);
        Assert.Equal(2, result.NewIntegral);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void NoCirculation_ReturnsToBaseCurveWithoutWindup(double flow)
    {
        var result = _regulator.Evaluate(Input() with { FlowLitresPerMinute = flow, Integral = 3, ObservedDeviationC = 1 });
        Assert.True(result.IsFallback);
        Assert.True(result.ShouldWrite);
        Assert.Equal(0, result.NewIntegral);
        Assert.Equal(0, result.RequestedDeviationC);
    }

    [Fact]
    public void CriticalColdRoom_WarmOtherRoomsCannotRequestNegativeOffset()
    {
        var result = _regulator.Evaluate(Input() with { CriticalRoomBelowMinimum = true, RepresentativeTemperatureErrorC = 2 });
        Assert.Equal(.5, result.RequestedDeviationC);
    }

    [Fact]
    public void CriticalColdRoom_NegativeOffsetIsNeutralizedWithoutWaitingThirtyMinutes()
    {
        var result = _regulator.Evaluate(Input() with
        {
            CriticalRoomBelowMinimum = true, ObservedDeviationC = -1, LastAcceptedWriteUtc = Now.AddMinutes(-1)
        });
        Assert.True(result.ShouldWrite);
        Assert.True(result.IsFallback);
        Assert.Equal(0, result.RequestedDeviationC);
    }

    [Theory]
    [InlineData("commissioning")]
    [InlineData("lease")]
    [InlineData("communication")]
    [InlineData("override")]
    [InlineData("sensor")]
    [InlineData("missing-telemetry")]
    [InlineData("old-telemetry")]
    [InlineData("future-telemetry")]
    [InlineData("future-evaluation")]
    [InlineData("future-write")]
    [InlineData("nan")]
    [InlineData("infinite-flow")]
    [InlineData("missing-flow")]
    [InlineData("negative-flow")]
    [InlineData("range")]
    [InlineData("step")]
    [InlineData("off-step")]
    [InlineData("integral")]
    public void SafetyFault_ReturnsZeroEvenDuringDhwAndRateLimit(string fault)
    {
        var input = Input() with { ObservedDeviationC = 1, Integral = 2, DhwActive = true, LastAcceptedWriteUtc = Now };
        input = fault switch
        {
            "commissioning" => input with { CommissioningVerified = false },
            "lease" => input with { WriterLeaseHeld = false },
            "communication" => input with { CommunicationHealthy = false },
            "override" => input with { ManualOverride = true },
            "sensor" => input with { SafetyInvalidReason = "Ogiltig rumsgivare" },
            "missing-telemetry" => input with { TelemetryUtc = null },
            "old-telemetry" => input with { TelemetryUtc = Now.AddMinutes(-11) },
            "future-telemetry" => input with { TelemetryUtc = Now.AddSeconds(1) },
            "future-evaluation" => input with { PreviousEvaluationUtc = Now.AddSeconds(1) },
            "future-write" => input with { LastAcceptedWriteUtc = Now.AddSeconds(1) },
            "nan" => input with { RepresentativeTemperatureErrorC = double.NaN },
            "infinite-flow" => input with { FlowLitresPerMinute = double.PositiveInfinity },
            "missing-flow" => input with { FlowLitresPerMinute = null },
            "negative-flow" => input with { FlowLitresPerMinute = -1 },
            "range" => input with { DeviationLimitC = 3 },
            "step" => input with { DeviationStepC = 2 },
            "off-step" => input with { ObservedDeviationC = .75 },
            "integral" => input with { Integral = double.NaN },
            _ => throw new ArgumentException(fault)
        };
        var result = _regulator.Evaluate(input);
        Assert.True(result.IsFallback);
        Assert.True(result.ShouldWrite);
        Assert.Equal(0, result.RequestedDeviationC);
        Assert.Equal(0, result.NewIntegral);
    }

    [Fact]
    public void SafetyFault_AlreadyNeutralDoesNotRepeatWrites()
    {
        var result = _regulator.Evaluate(Input() with { CommunicationHealthy = false });
        Assert.False(result.ShouldWrite);
        Assert.True(result.IsFallback);
    }

    private static ConservativeLwtInput Input() => new(
        Now, Now.AddMinutes(-5), Now.AddMinutes(-5), Now.AddHours(-1),
        0, 0, false, false, false, 12, 0, true, true, true, false);
}
