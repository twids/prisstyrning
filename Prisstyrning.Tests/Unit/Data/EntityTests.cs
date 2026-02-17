using Prisstyrning.Data.Entities;

namespace Prisstyrning.Tests.Unit.Data;

public class EntityTests
{
    [Fact]
    public void UserSettings_DefaultValues_AreCorrect()
    {
        var entity = new UserSettings();

        Assert.Equal(string.Empty, entity.UserId);
        Assert.Equal(3, entity.ComfortHours);
        Assert.Equal(0.9, entity.TurnOffPercentile);
        Assert.Equal(28, entity.MaxComfortGapHours);
        Assert.False(entity.AutoApplySchedule);
        Assert.Equal("SE3", entity.Zone);
    }

    [Fact]
    public void AdminRole_DefaultValues_AreCorrect()
    {
        var entity = new AdminRole();

        Assert.Equal(string.Empty, entity.UserId);
        Assert.False(entity.IsAdmin);
        Assert.False(entity.HasHangfireAccess);
    }

    [Fact]
    public void PriceSnapshot_DefaultValues_AreCorrect()
    {
        var entity = new PriceSnapshot();

        Assert.Equal(0, entity.Id);
        Assert.Equal(string.Empty, entity.Zone);
        Assert.Equal("[]", entity.TodayPricesJson);
        Assert.Equal("[]", entity.TomorrowPricesJson);
    }

    [Fact]
    public void ScheduleHistoryEntry_DefaultValues_AreCorrect()
    {
        var entity = new ScheduleHistoryEntry();

        Assert.Equal(0, entity.Id);
        Assert.Equal(string.Empty, entity.UserId);
        Assert.Equal("{}", entity.SchedulePayloadJson);
    }

    [Fact]
    public void DaikinToken_DefaultValues_AreCorrect()
    {
        var entity = new DaikinToken();

        Assert.Equal(string.Empty, entity.UserId);
        Assert.Equal(string.Empty, entity.AccessToken);
        Assert.Equal(string.Empty, entity.RefreshToken);
    }
}
