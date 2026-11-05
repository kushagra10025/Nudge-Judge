using NudgeJudge.Core.Models;
using NudgeJudge.Core.Rules;

namespace NudgeJudge.Core.Tests;

public class TimeConditionTests
{
    private sealed class Boot : ISystemBoot
    {
        public string CurrentBootId => "b";
        public bool IsCurrentBoot(string bootId) => bootId == "b";
    }

    /// <summary>A clock fixed at a given local time (in UTC, so the tests don't depend on the machine's zone).</summary>
    private sealed class FixedTime(DateTime local) : TimeProvider
    {
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
        public override DateTimeOffset GetUtcNow() => new(DateTime.SpecifyKind(local, DateTimeKind.Utc));
    }

    // 2026-11-02 is a Monday.
    private static DateTime At(DayOfWeek day, int hour, int minute = 0) =>
        new DateTime(2026, 11, 1, hour, minute, 0).AddDays((int)day);

    private static bool Matches(string window, DateTime at)
    {
        var (predicate, error) = TimeWindow.Compile(window);
        Assert.Null(error);
        return predicate!(at);
    }

    [Theory]
    [InlineData("09:00-17:00", DayOfWeek.Wednesday, 9, 0, true)]
    [InlineData("09:00-17:00", DayOfWeek.Wednesday, 16, 59, true)]
    [InlineData("09:00-17:00", DayOfWeek.Wednesday, 17, 0, false)] // end is exclusive
    [InlineData("09:00-17:00", DayOfWeek.Sunday, 8, 59, false)]
    [InlineData("9-17", DayOfWeek.Sunday, 12, 0, true)]
    [InlineData("9am-5pm", DayOfWeek.Sunday, 12, 0, true)]
    [InlineData("9 to 5pm", DayOfWeek.Sunday, 16, 30, true)]
    [InlineData("Mon-Fri 09:00-17:00", DayOfWeek.Friday, 10, 0, true)]
    [InlineData("Mon-Fri 09:00-17:00", DayOfWeek.Saturday, 10, 0, false)]
    [InlineData("weekdays 9-17", DayOfWeek.Monday, 10, 0, true)]
    [InlineData("Sat,Sun", DayOfWeek.Sunday, 3, 0, true)]
    [InlineData("weekends", DayOfWeek.Tuesday, 3, 0, false)]
    [InlineData("Fri-Mon", DayOfWeek.Sunday, 3, 0, true)] // wraps round the week
    [InlineData("Fri-Mon", DayOfWeek.Wednesday, 3, 0, false)]
    [InlineData("22:00-06:00", DayOfWeek.Tuesday, 23, 0, true)]
    [InlineData("22:00-06:00", DayOfWeek.Tuesday, 5, 59, true)]
    [InlineData("22:00-06:00", DayOfWeek.Tuesday, 12, 0, false)]
    [InlineData("Fri 22:00-02:00", DayOfWeek.Saturday, 1, 0, true)] // the night starting Friday
    [InlineData("Fri 22:00-02:00", DayOfWeek.Friday, 1, 0, false)]  // the night starting Thursday
    [InlineData("18:00-24:00", DayOfWeek.Monday, 23, 59, true)]
    public void Windows_match_local_time(string window, DayOfWeek day, int hour, int minute, bool expected) =>
        Assert.Equal(expected, Matches(window, At(day, hour, minute)));

    [Theory]
    [InlineData("")]
    [InlineData("25:00-26:00")]
    [InlineData("09:00")]
    [InlineData("09:00-09:00")]
    [InlineData("Funday 9-17")]
    [InlineData("9:75-10")]
    [InlineData("13pm-2pm")]
    public void Bad_windows_are_errors(string window) => Assert.NotNull(TimeWindow.Compile(window).Error);

    /// <summary>9 to 5 on weekdays GitHub opens in Chrome; any other time it falls through to Firefox.</summary>
    [Theory]
    [InlineData(DayOfWeek.Monday, 10, "GitHub at work")]
    [InlineData(DayOfWeek.Monday, 18, "GitHub")]
    [InlineData(DayOfWeek.Saturday, 10, "GitHub")]
    public void Time_condition_routes_by_time_of_day(DayOfWeek day, int hour, string expected)
    {
        List<Rule> rules =
        [
            new()
            {
                Name = "GitHub at work",
                Conditions = [RuleCondition.Link(MatchKind.Domain, "github.com"), RuleCondition.Time("Mon-Fri 09:00-17:00")],
                BrowserId = Guid.NewGuid(),
            },
            new() { Name = "GitHub", Conditions = [RuleCondition.Link(MatchKind.Domain, "github.com")], BrowserId = Guid.NewGuid() },
        ];
        var engine = new RuleEngine(new Boot(), new FixedTime(At(day, hour)));
        Assert.Equal(expected, engine.Match(new Uri("https://github.com/acme"), rules).Rule?.Name);
        Assert.Equal(expected, engine.FindMatch(new Uri("https://github.com/acme"), engine.Prepare(rules)).Rule?.Name);
    }

    [Fact]
    public void Negated_time_matches_outside_the_window()
    {
        var condition = RuleCondition.Time("09:00-17:00");
        condition.Negate = true;
        var rule = new Rule { Name = "After hours", Conditions = [condition] };
        var engine = new RuleEngine(new Boot(), new FixedTime(At(DayOfWeek.Monday, 20)));
        Assert.True(engine.Match(new Uri("https://example.com/"), [rule]).IsMatch);
    }

    [Fact]
    public void Blank_time_condition_is_an_error()
    {
        var rule = new Rule { Name = "x", Conditions = [RuleCondition.Time("")] };
        var result = new RuleEngine(new Boot()).Compile(rule);
        Assert.False(result.Success);
        Assert.Contains("Enter a time", result.Error);
    }
}

public class TimeWindowDescribeTests
{
    [Theory]
    [InlineData("Mon-Fri 09:00-17:00", "Weekdays, 09:00 to 17:00")]
    [InlineData("9-5", "Every day, 09:00 to 05:00 the next day (overnight)")]
    [InlineData("9am-5pm", "Every day, 09:00 to 17:00")]
    [InlineData("weekends", "Weekends, all day")]
    [InlineData("Fri 22:00-02:00", "Fri, 22:00 to 02:00 the next day (overnight)")]
    [InlineData("Mon,Wed,Fri 18-24", "Mon, Wed, Fri, 18:00 to midnight")]
    [InlineData("Mon-Thu 8-12", "Mon to Thu, 08:00 to 12:00")]
    [InlineData("Sat,Sun,Mon", "Mon, Sat, Sun, all day")]
    public void Valid_windows_are_read_back_in_words(string value, string expected) =>
        Assert.Equal(expected, TimeWindow.Describe(value));

    [Theory]
    [InlineData("")]
    [InlineData("Funday")]
    [InlineData("25-26")]
    public void Invalid_windows_have_no_reading(string value) => Assert.Null(TimeWindow.Describe(value));
}

public class CopyNameTests
{
    [Theory]
    [InlineData("GitHub", new string[0], "GitHub (copy)")]
    [InlineData("GitHub", new[] { "GitHub (copy)" }, "GitHub (copy 2)")]
    [InlineData("GitHub (copy)", new[] { "GitHub", "GitHub (copy)" }, "GitHub (copy 2)")]
    [InlineData("GitHub (copy 2)", new[] { "GitHub (copy)", "GitHub (copy 2)" }, "GitHub (copy 3)")]
    public void Copies_count_up_instead_of_stacking(string name, string[] taken, string expected) =>
        Assert.Equal(expected, Rule.CopyName(name, taken));
}
