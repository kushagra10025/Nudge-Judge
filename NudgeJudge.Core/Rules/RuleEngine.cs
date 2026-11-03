using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.RegularExpressions;
using NudgeJudge.Core.Models;

namespace NudgeJudge.Core.Rules;

public enum RuleEvaluationState
{
    /// <summary>Evaluation stopped before reaching this rule.</summary>
    NotReached,
    /// <summary>Disabled or expired.</summary>
    Inactive,
    NoMatch,
    Matched,
    /// <summary>Invalid pattern or the match timed out.</summary>
    Error,
}

public readonly record struct RuleEvaluation(Rule Rule, RuleEvaluationState State, string? Error = null);

public sealed record MatchResult(Uri Url, Rule? Rule, int RuleIndex, IReadOnlyList<RuleEvaluation> Evaluations, TimeSpan Elapsed)
{
    public bool IsMatch => Rule is not null;
}

/// <summary>Ordered, first-match-wins rule evaluation with a compiled-pattern cache.</summary>
public sealed class RuleEngine(ISystemBoot boot, TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly ConcurrentDictionary<(MatchKind, string, bool), PatternCompileResult> _cache = new();

    public PatternCompileResult Compile(Rule rule) => Compile(rule.MatchKind, rule.Pattern, rule.CaseSensitive);

    public PatternCompileResult Compile(MatchKind type, string pattern, bool caseSensitive)
    {
        if (_cache.Count > 2048) _cache.Clear(); // bound memory if patterns churn while editing
        return _cache.GetOrAdd((type, pattern ?? "", caseSensitive),
            static k => PatternCompiler.Compile(k.Item1, k.Item2, k.Item3));
    }

    public MatchResult Match(Uri url, IReadOnlyList<Rule> rules)
    {
        var started = Stopwatch.GetTimestamp();
        var now = _time.GetUtcNow();
        var evaluations = new RuleEvaluation[rules.Count];
        Rule? winner = null;
        var winnerIndex = -1;

        for (var i = 0; i < rules.Count; i++)
        {
            var rule = rules[i];
            if (winner is not null)
            {
                evaluations[i] = new(rule, RuleEvaluationState.NotReached);
                continue;
            }
            if (!rule.IsActive(now, boot))
            {
                evaluations[i] = new(rule, RuleEvaluationState.Inactive);
                continue;
            }

            var compiled = Compile(rule);
            if (!compiled.Success)
            {
                evaluations[i] = new(rule, RuleEvaluationState.Error, compiled.Error);
                continue;
            }

            try
            {
                if (compiled.Pattern!.IsMatch(url))
                {
                    winner = rule;
                    winnerIndex = i;
                    evaluations[i] = new(rule, RuleEvaluationState.Matched);
                }
                else
                {
                    evaluations[i] = new(rule, RuleEvaluationState.NoMatch);
                }
            }
            catch (RegexMatchTimeoutException)
            {
                evaluations[i] = new(rule, RuleEvaluationState.Error, "Match timed out and was skipped.");
            }
        }

        return new MatchResult(url, winner, winnerIndex, evaluations, Stopwatch.GetElapsedTime(started));
    }
}
