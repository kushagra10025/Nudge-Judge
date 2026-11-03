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
    /// <summary>Invalid condition or the match timed out.</summary>
    Error,
}

public readonly record struct RuleEvaluation(Rule Rule, RuleEvaluationState State, string? Error = null);

public sealed record MatchResult(Uri Url, SourceApp? Source, Rule? Rule, int RuleIndex, IReadOnlyList<RuleEvaluation> Evaluations, TimeSpan Elapsed)
{
    public bool IsMatch => Rule is not null;
}

/// <summary>What a rule is tested against: the link, and the app it came from when known.</summary>
public readonly record struct LinkContext(Uri Url, SourceApp? Source);

/// <summary>A rule's conditions, compiled into one predicate.</summary>
public sealed class CompiledRule
{
    private readonly Func<LinkContext, bool> _predicate;
    internal CompiledRule(Func<LinkContext, bool> predicate) => _predicate = predicate;

    /// <exception cref="RegexMatchTimeoutException">A pattern exceeded its time budget.</exception>
    public bool IsMatch(LinkContext link) => _predicate(link);
}

/// <summary>One condition's compile outcome. <see cref="ValueErrors"/> lines up with the condition's values.</summary>
public sealed record ConditionCompileResult(string? Error, IReadOnlyList<string?> ValueErrors)
{
    public bool Success => Error is null;
}

public sealed record RuleCompileResult(CompiledRule? Rule, string? Error, IReadOnlyList<ConditionCompileResult> Conditions)
{
    public bool Success => Rule is not null;
}

/// <summary>Ordered, first-match-wins rule evaluation with a compiled-pattern cache.</summary>
public sealed class RuleEngine(ISystemBoot boot, TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly ConcurrentDictionary<(MatchKind, string, bool), PatternCompileResult> _cache = new();
    private readonly ConcurrentDictionary<string, SourceAppCompileResult> _sourceCache = new(StringComparer.Ordinal);

    public PatternCompileResult Compile(MatchKind type, string pattern, bool caseSensitive)
    {
        if (_cache.Count > 2048) _cache.Clear(); // bound memory if patterns churn while editing
        return _cache.GetOrAdd((type, pattern ?? "", caseSensitive),
            static k => PatternCompiler.Compile(k.Item1, k.Item2, k.Item3));
    }

    public SourceAppCompileResult CompileSource(string value)
    {
        if (_sourceCache.Count > 2048) _sourceCache.Clear();
        return _sourceCache.GetOrAdd(value ?? "", static v => SourceAppPattern.Compile(v));
    }

    /// <summary>Compiles one condition. Blank values are ignored; a condition needs at least one other.</summary>
    public (Func<LinkContext, bool>? Predicate, ConditionCompileResult Result) Compile(RuleCondition condition)
    {
        var errors = new string?[condition.Values.Count];
        var tests = new List<Func<LinkContext, bool>>();
        string? first = null;

        for (var i = 0; i < condition.Values.Count; i++)
        {
            var value = condition.Values[i];
            if (string.IsNullOrWhiteSpace(value)) continue;

            if (condition.Field == ConditionField.SourceApp)
            {
                var compiled = CompileSource(value);
                if (compiled.Success)
                {
                    var p = compiled.Predicate!;
                    tests.Add(link => p(link.Source));
                }
                errors[i] = compiled.Error;
            }
            else
            {
                var compiled = Compile(condition.MatchKind, value, condition.CaseSensitive);
                if (compiled.Success)
                {
                    var p = compiled.Pattern!;
                    tests.Add(link => p.IsMatch(link.Url));
                }
                errors[i] = compiled.Error;
            }
            first ??= errors[i] is { } e ? (Count(condition) > 1 ? $"“{value.Trim()}”: {e}" : e) : null;
        }

        if (first is null && tests.Count == 0)
            first = condition.Field == ConditionField.SourceApp ? "Enter an app, e.g. ms-teams.exe." : "Enter a pattern.";
        if (first is not null) return (null, new ConditionCompileResult(first, errors));

        var negate = condition.Negate;
        var all = tests.ToArray();
        return (link =>
        {
            foreach (var t in all)
                if (t(link)) return !negate;
            return negate;
        }, new ConditionCompileResult(null, errors));
    }

    public RuleCompileResult Compile(Rule rule)
    {
        var results = new List<ConditionCompileResult>(rule.Conditions.Count);
        var predicates = new List<Func<LinkContext, bool>>(rule.Conditions.Count);
        string? error = null;

        for (var i = 0; i < rule.Conditions.Count; i++)
        {
            var (predicate, result) = Compile(rule.Conditions[i]);
            results.Add(result);
            if (predicate is not null) predicates.Add(predicate);
            else error ??= rule.Conditions.Count > 1 ? $"Condition {i + 1}: {result.Error}" : result.Error;
        }

        if (rule.Conditions.Count == 0) error = "Add a condition.";
        if (error is not null) return new RuleCompileResult(null, error, results);

        var all = predicates.ToArray();
        Func<LinkContext, bool> combined = rule.Join == RuleJoin.Any
            ? link => { foreach (var p in all) if (p(link)) return true; return false; }
            : link => { foreach (var p in all) if (!p(link)) return false; return true; };
        return new RuleCompileResult(new CompiledRule(combined), null, results);
    }

    public MatchResult Match(Uri url, IReadOnlyList<Rule> rules, SourceApp? source = null)
    {
        var started = Stopwatch.GetTimestamp();
        var now = _time.GetUtcNow();
        var link = new LinkContext(url, source);
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
                if (compiled.Rule!.IsMatch(link))
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

        return new MatchResult(url, source, winner, winnerIndex, evaluations, Stopwatch.GetElapsedTime(started));
    }

    private static int Count(RuleCondition c) => c.Values.Count(v => !string.IsNullOrWhiteSpace(v));
}
