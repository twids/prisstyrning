using Prisstyrning.Thermal.Control;
using Prisstyrning.Thermal.Domain;

namespace Prisstyrning.Tests.Thermal;

public class LwtRegulatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Evaluate_StaleTelemetryImmediatelyRequestsZero()
    {
        var decision = new LwtRegulator().Evaluate(ValidInput() with
        {
            TelemetryUtc = Now.AddMinutes(-11),
            CurrentDeviationC = 1
        });

        Assert.True(decision.ShouldWrite);
        Assert.True(decision.IsFallback);
        Assert.Equal(0, decision.RequestedDeviationC);
        Assert.Contains("tio minuter", decision.Reason);
    }

    [Fact]
    public void Evaluate_FreezesDuringDhw()
    {
        var decision = new LwtRegulator().Evaluate(ValidInput() with
        {
            DhwActive = true,
            CurrentDeviationC = 0.5,
            PlannedDeviationC = 1
        });

        Assert.False(decision.ShouldWrite);
        Assert.Equal(0.5, decision.RequestedDeviationC);
        Assert.Contains("DHW", decision.Reason);
    }

    [Fact]
    public void Evaluate_FreezesDuringDefrost()
    {
        var decision = new LwtRegulator().Evaluate(ValidInput() with
        {
            DefrostActive = true,
            CurrentDeviationC = 0.5,
            PlannedDeviationC = 1
        });

        Assert.False(decision.ShouldWrite);
        Assert.Equal(0.5, decision.RequestedDeviationC);
        Assert.Contains("avfrostning", decision.Reason);
    }

    [Fact]
    public void Evaluate_RespectsThirtyMinuteRateLimitAndHalfDegreeThreshold()
    {
        var rateLimited = new LwtRegulator().Evaluate(ValidInput() with
        {
            LastWriteUtc = Now.AddMinutes(-10),
            PlannedDeviationC = 1
        });
        var insignificant = new LwtRegulator().Evaluate(ValidInput() with
        {
            LastWriteUtc = Now.AddMinutes(-31),
            PlannedDeviationC = 0.2
        });

        Assert.False(rateLimited.ShouldWrite);
        Assert.False(insignificant.ShouldWrite);
    }

    [Fact]
    public void Evaluate_CriticalRoomCannotBeSacrificedForNegativePlan()
    {
        var decision = new LwtRegulator().Evaluate(ValidInput() with
        {
            CriticalRoomBelowMinimum = true,
            RepresentativeTemperatureErrorC = -0.7,
            PlannedDeviationC = -1,
            LastWriteUtc = Now.AddHours(-1)
        });

        Assert.True(decision.RequestedDeviationC >= -0.5);
    }

    [Fact]
    public void Evaluate_LowFlowRequestsSafeZero()
    {
        var decision = new LwtRegulator().Evaluate(ValidInput() with
        {
            FlowLitresPerMinute = 1,
            CurrentDeviationC = 1
        });

        Assert.True(decision.ShouldWrite);
        Assert.True(decision.IsFallback);
        Assert.Equal(0, decision.RequestedDeviationC);
        Assert.Contains("Flödet", decision.Reason);
    }

    [Fact]
    public void Evaluate_LostWriterLeaseRequestsSafeZero()
    {
        var decision = new LwtRegulator().Evaluate(ValidInput() with
        {
            WriterLeaseHeld = false,
            CurrentDeviationC = -1
        });

        Assert.True(decision.ShouldWrite);
        Assert.True(decision.IsFallback);
        Assert.Equal(0, decision.RequestedDeviationC);
        Assert.Contains("leasen", decision.Reason);
    }

    [Fact]
    public void Evaluate_ManualOverrideRequestsSafeZero()
    {
        var decision = new LwtRegulator().Evaluate(ValidInput() with
        {
            ManualOverride = true,
            CurrentDeviationC = 0.5
        });

        Assert.True(decision.ShouldWrite);
        Assert.True(decision.IsFallback);
        Assert.Equal(0, decision.RequestedDeviationC);
        Assert.Contains("override", decision.Reason);
    }

    [Fact]
    public void Evaluate_StalePlanRequestsSafeZero()
    {
        var decision = new LwtRegulator().Evaluate(ValidInput() with
        {
            PlanCreatedUtc = Now.AddMinutes(-61),
            CurrentDeviationC = 1
        });

        Assert.True(decision.ShouldWrite);
        Assert.True(decision.IsFallback);
        Assert.Equal(0, decision.RequestedDeviationC);
        Assert.Contains("60 minuter", decision.Reason);
    }

    [Fact]
    public void Evaluate_InvalidPlanEvidenceImmediatelyRequestsSafeZero()
    {
        var decision = new LwtRegulator().Evaluate(ValidInput() with
        {
            SafetyInvalidReason = "Planens verifierade underlag gäller inte längre.",
            CurrentDeviationC = 1
        });

        Assert.True(decision.ShouldWrite);
        Assert.True(decision.IsFallback);
        Assert.Equal(0, decision.RequestedDeviationC);
        Assert.Contains("underlag", decision.Reason);
    }

    [Theory]
    [InlineData("current")]
    [InlineData("planned")]
    [InlineData("integral")]
    [InlineData("limit")]
    public void Evaluate_NonFiniteSafetyInputImmediatelyRequestsSafeZero(string fault)
    {
        var input = ValidInput();
        input = fault switch
        {
            "current" => input with { CurrentDeviationC = double.NaN },
            "planned" => input with { PlannedDeviationC = double.PositiveInfinity },
            "integral" => input with { Integral = double.NegativeInfinity },
            "limit" => input with { DeviationLimitC = double.NaN },
            _ => throw new InvalidOperationException()
        };

        var decision = new LwtRegulator().Evaluate(input);

        Assert.Equal(fault == "current", decision.ShouldWrite);
        Assert.True(decision.IsFallback);
        Assert.Equal(0, decision.RequestedDeviationC);
        Assert.Contains("säkerhetsunderlag", decision.Reason);
    }

    [Fact]
    public void Evaluate_P1P2FailureRequestsSafeZero()
    {
        var decision = new LwtRegulator().Evaluate(ValidInput() with
        {
            P1P2Healthy = false,
            CurrentDeviationC = 1
        });

        Assert.True(decision.ShouldWrite);
        Assert.True(decision.IsFallback);
        Assert.Equal(0, decision.RequestedDeviationC);
        Assert.Contains("P1P2MQTT", decision.Reason);
    }

    [Fact]
    public void Evaluate_AntiWindupAndConfiguredDeviationLimitAreEnforced()
    {
        var decision = new LwtRegulator().Evaluate(ValidInput() with
        {
            RepresentativeTemperatureErrorC = -100,
            PlannedDeviationC = 3,
            Integral = 9.9,
            DeviationLimitC = 1,
            LastWriteUtc = Now.AddHours(-1)
        });

        Assert.Equal(10, decision.NewIntegral);
        Assert.Equal(1, decision.RequestedDeviationC);
    }

    private static LwtRegulatorInput ValidInput() => new(
        ControlMode.LwtActive,
        Now,
        Now.AddMinutes(-5),
        Now.AddMinutes(-15),
        0,
        0,
        false,
        false,
        false,
        12,
        true,
        true,
        false,
        0,
        Now.AddHours(-1),
        0,
        1);
}
