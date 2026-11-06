using LolHighlight.Recording.Models;
using LolHighlight.Recording.Services;

namespace LolHighlight.Desktop.Tests;

public class HighlightEvaluatorTests
{
    private readonly HighlightEvaluator _evaluator = new();

    [Fact]
    public void Evaluate_ChampionKill_ReturnsTrigger()
    {
        var player = new PlayerIdentity("riotId", "TestPlayer", "TAG");
        var evt = new Dictionary<string, object>
        {
            ["EventName"] = "ChampionKill",
            ["EventID"] = 1,
            ["EventTime"] = 10.5,
            ["KillerName"] = "TestPlayer",
            ["VictimName"] = "EnemyPlayer"
        };

        var result = _evaluator.Evaluate(evt, player);

        Assert.NotNull(result);
        Assert.Equal("kill", result.Kind);
        Assert.Equal("击杀 EnemyPlayer", result.Label);
        Assert.Equal(1, result.EventId);
        Assert.Equal(10.5, result.EventTime);
    }

    [Fact]
    public void Evaluate_Multikill_ReturnsTrigger()
    {
        var player = new PlayerIdentity("riotId", "TestPlayer", "TAG");
        var evt = new Dictionary<string, object>
        {
            ["EventName"] = "Multikill",
            ["EventID"] = 2,
            ["EventTime"] = 15.0,
            ["KillerName"] = "TestPlayer",
            ["KillStreak"] = 3
        };

        var result = _evaluator.Evaluate(evt, player);

        Assert.NotNull(result);
        Assert.Equal("multikill_3", result.Kind);
        Assert.Equal("三杀", result.Label);
    }

    [Fact]
    public void Evaluate_FirstBlood_ReturnsTrigger()
    {
        var player = new PlayerIdentity("riotId", "TestPlayer", "TAG");
        var evt = new Dictionary<string, object>
        {
            ["EventName"] = "FirstBlood",
            ["EventID"] = 3,
            ["EventTime"] = 5.0,
            ["Recipient"] = "TestPlayer"
        };

        var result = _evaluator.Evaluate(evt, player);

        Assert.NotNull(result);
        Assert.Equal("first_blood", result.Kind);
        Assert.Equal("第一滴血", result.Label);
    }

    [Fact]
    public void Evaluate_Ace_ReturnsTrigger()
    {
        var player = new PlayerIdentity("riotId", "TestPlayer", "TAG");
        var evt = new Dictionary<string, object>
        {
            ["EventName"] = "Ace",
            ["EventID"] = 4,
            ["EventTime"] = 20.0,
            ["Acer"] = "TestPlayer"
        };

        var result = _evaluator.Evaluate(evt, player);

        Assert.NotNull(result);
        Assert.Equal("ace", result.Kind);
        Assert.Equal("团灭", result.Label);
    }

    [Fact]
    public void Evaluate_DragonKill_Involved_ReturnsTrigger()
    {
        var player = new PlayerIdentity("riotId", "TestPlayer", "TAG");
        var evt = new Dictionary<string, object>
        {
            ["EventName"] = "DragonKill",
            ["EventID"] = 5,
            ["EventTime"] = 25.0,
            ["KillerName"] = "EnemyPlayer",
            ["Assisters"] = new[] { "TestPlayer", "Teammate" }
        };

        var result = _evaluator.Evaluate(evt, player);

        Assert.NotNull(result);
        Assert.Equal("dragonkill", result.Kind);
        Assert.Equal("参与小龙", result.Label);
    }

    [Fact]
    public void Evaluate_DragonKill_Stolen_ReturnsTrigger()
    {
        var player = new PlayerIdentity("riotId", "TestPlayer", "TAG");
        var evt = new Dictionary<string, object>
        {
            ["EventName"] = "DragonKill",
            ["EventID"] = 6,
            ["EventTime"] = 30.0,
            ["KillerName"] = "TestPlayer",
            ["Stolen"] = "True"
        };

        var result = _evaluator.Evaluate(evt, player);

        Assert.NotNull(result);
        Assert.Equal("dragonkill", result.Kind);
        Assert.Equal("抢下小龙", result.Label);
    }

    [Fact]
    public void Evaluate_UnrelatedEvent_ReturnsNull()
    {
        var player = new PlayerIdentity("riotId", "TestPlayer", "TAG");
        var evt = new Dictionary<string, object>
        {
            ["EventName"] = "ChampionKill",
            ["EventID"] = 7,
            ["EventTime"] = 35.0,
            ["KillerName"] = "OtherPlayer",
            ["VictimName"] = "AnotherPlayer"
        };

        var result = _evaluator.Evaluate(evt, player);

        Assert.Null(result);
    }

    [Fact]
    public void Evaluate_CaseInsensitive_ReturnsTrigger()
    {
        var player = new PlayerIdentity("riotId", "TestPlayer", "TAG");
        var evt = new Dictionary<string, object>
        {
            ["EventName"] = "ChampionKill",
            ["EventID"] = 8,
            ["EventTime"] = 40.0,
            ["KillerName"] = "testplayer",
            ["VictimName"] = "Enemy"
        };

        var result = _evaluator.Evaluate(evt, player);

        Assert.NotNull(result);
        Assert.Equal("kill", result.Kind);
    }

    [Fact]
    public void Evaluate_TurretKilled_Involved_ReturnsTrigger()
    {
        var player = new PlayerIdentity("riotId", "TestPlayer", "TAG");
        var evt = new Dictionary<string, object>
        {
            ["EventName"] = "TurretKilled",
            ["EventID"] = 9,
            ["EventTime"] = 45.0,
            ["KillerName"] = "Teammate",
            ["Assisters"] = new[] { "TestPlayer" }
        };

        var result = _evaluator.Evaluate(evt, player);

        Assert.NotNull(result);
        Assert.Equal("turretkilled", result.Kind);
        Assert.Equal("参与防御塔", result.Label);
    }
}
