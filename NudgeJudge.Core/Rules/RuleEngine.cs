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

/// <summary>What a rule is tested against: the link, the app it came from when known, and the local time (now when null).</summary>
public readonly record struct LinkContext(Uri Url, SourceApp? Source, DateTime? LocalTime = null);

/// <summary>A rule's conditions, compiled into one predicate.</summary>
public sealed class CompiledRule
{
    private readonly Func<LinkInput, bool> _predicate;
    internal CompiledRule(Func<LinkInput, bool> predicate) => _predicate = predicate;

    /// <exception cref="RegexMatchTimeoutException">A pattern exceeded its time budget.</exception>
    public bool IsMatch(LinkContext link) => _predicate(new LinkInput(link.Url, link.Source, link.LocalTime));

    internal bool IsMatch(LinkInput link) => _predicate(link);
}

/// <summary>
/// One condition's compile outcome. <see cref="ValueErrors"/> lines up with the condition's values; for a
/// group, <see cref="Children"/> lines up with the group's conditions.
/// </summary>
public sealed record ConditionCompileResult(string? Error, IReadOnlyList<string?> ValueErrors, IReadOnlyList<ConditionCompileResult>? Children = null)
{
    public bool Success => Error is null;
}

/// <summary>
/// A rule list made ready for repeated matching: matching skips the check that each rule still looks
/// the way it did when compiled. Holds a copy of the list, so prepare a new one whenever the rules change.
/// Rules are compiled the first time matching reaches them, so a link that matches early pays for nothing below.
/// </summary>
public sealed class PreparedRules
{
    internal PreparedRules(IReadOnlyList<Rule> rules)
    {
        Rules = [.. rules];
        Compiled = new RuleCompileResult?[Rules.Length];
    }

    internal Rule[] Rules { get; }

    /// <summary>Filled in as matching reaches each rule. Two threads may both fill a slot; they'd store the same thing.</summary>
    internal RuleCompileResult?[] Compiled { get; }
}

/// <summary>A rule's compiled form, with a copy of the conditions it was compiled from.</summary>
internal sealed record CompiledRuleCache(RuleJoin Join, List<RuleCondition> Conditions, RuleCompileResult Result);

public sealed record RuleCompileResult(CompiledRule? Rule, string? Error, IReadOnlyList<ConditionCompileResult> Conditions)
{
    public bool Success => Rule is not null;
}

/// <summary>
/// Ordered, first-match-wins rule evaluation. Each rule is compiled once into a tree of string comparisons
/// and kept until its contents change, so matching a link does no parsing and allocates almost nothing.
/// </summary>
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

    /// <summary>Compiles one condition or group. Blank values are ignored; a condition needs at least one other.</summary>
    internal (Func<LinkInput, bool>? Predicate, ConditionCompileResult Result) Compile(RuleCondition condition) =>
        Compile(condition, depth: 2);

    /// <param name="depth">The level this condition sits at: 2 directly in a rule, 3 inside a group.</param>
    private (Func<LinkInput, bool>? Predicate, ConditionCompileResult Result) Compile(RuleCondition condition, int depth)
    {
        if (condition.IsGroup) return CompileGroup(condition, depth);

        var errors = new string?[condition.Values.Count];
        var tests = new List<Func<LinkInput, bool>>();
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
            else if (condition.Field == ConditionField.Time)
            {
                var (p, error) = TimeWindow.Compile(value);
                if (p is not null) tests.Add(link => p(link.LocalTime));
                errors[i] = error;
            }
            else
            {
                var compiled = Compile(condition.MatchKind, value, condition.CaseSensitive);
                if (compiled.Success)
                {
                    var p = compiled.Pattern!;
                    tests.Add(p.Predicate);
                }
                errors[i] = compiled.Error;
            }
            first ??= errors[i] is { } e ? (Count(condition) > 1 ? $"“{value.Trim()}”: {e}" : e) : null;
        }

        if (first is null && tests.Count == 0)
            first = condition.Field switch
            {
                ConditionField.SourceApp => "Enter an app, e.g. ms-teams.exe.",
                ConditionField.Time => $"Enter a time, e.g. {TimeWindow.Example}.",
                _ => "Enter a pattern.",
            };
        if (first is not null) return (null, new ConditionCompileResult(first, errors));

        var any = AnyOf(tests);
        return (condition.Negate ? link => !any(link) : any, new ConditionCompileResult(null, errors));
    }

    /// <summary>Compiles a rule, or returns the compiled form from last time if the rule hasn't changed since.</summary>
    public RuleCompileResult Compile(Rule rule)
    {
        // Rules are edited in place, so the cache keeps a copy of what it was compiled from and is rebuilt
        // once the rule no longer looks like that. A compiled rule depends on nothing but the conditions.
        if (rule.Compiled is { } cached && cached.Join == rule.Join && SameConditions(cached.Conditions, rule.Conditions))
            return cached.Result;

        var result = CompileFresh(rule);
        rule.Compiled = new CompiledRuleCache(rule.Join, rule.Conditions.Select(c => c.Clone()).ToList(), result);
        return result;
    }

    private RuleCompileResult CompileFresh(Rule rule)
    {
        var (predicate, results, error) = CompileAll(rule.Conditions, rule.Join, depth: 2);
        if (rule.Conditions.Count == 0) error = "Add a condition.";
        if (error is not null) return new RuleCompileResult(null, error, results);
        return new RuleCompileResult(new CompiledRule(predicate!), null, results);
    }

    private (Func<LinkInput, bool>? Predicate, ConditionCompileResult Result) CompileGroup(RuleCondition group, int depth)
    {
        if (depth > RuleCondition.MaxDepth)
            return (null, new ConditionCompileResult("Groups can only go two levels deep.", []));

        var conditions = group.Conditions ?? [];
        var (predicate, results, error) = CompileAll(conditions, group.Join, depth + 1);
        if (conditions.Count == 0) error = "Add a condition to this group.";
        return (error is null ? predicate : null, new ConditionCompileResult(error, [], results));
    }

    /// <summary>
    /// Compiles conditions joined by all/any. Errors name the condition by its position, with groups
    /// numbered like outlines: "Condition 3.2: Enter a pattern."
    /// </summary>
    private (Func<LinkInput, bool>? Predicate, List<ConditionCompileResult> Results, string? Error) CompileAll(
        List<RuleCondition> conditions, RuleJoin join, int depth)
    {
        var results = new List<ConditionCompileResult>(conditions.Count);
        var predicates = new List<Func<LinkInput, bool>>(conditions.Count);
        string? error = null;

        for (var i = 0; i < conditions.Count; i++)
        {
            var (predicate, result) = Compile(conditions[i], depth);
            results.Add(result);
            if (predicate is not null) predicates.Add(predicate);
            else error ??= conditions.Count > 1 || conditions[i].IsGroup || depth > 2 ? Locate(i + 1, result.Error!) : result.Error;
        }
        if (error is not null) return (null, results, error);

        return (join == RuleJoin.Any ? AnyOf(predicates) : AllOf(predicates), results, null);
    }

    // A single test needs no wrapper, which keeps the common one-condition, one-value rule a single call deep.

    private static Func<LinkInput, bool> AnyOf(List<Func<LinkInput, bool>> tests)
    {
        if (tests.Count == 1) return tests[0];
        if (tests.Count == 2)
        {
            var (a, b) = (tests[0], tests[1]);
            return link => a(link) || b(link);
        }
        var all = tests.ToArray();
        return link =>
        {
            foreach (var t in all)
                if (t(link)) return true;
            return false;
        };
    }

    private static Func<LinkInput, bool> AllOf(List<Func<LinkInput, bool>> tests)
    {
        if (tests.Count == 1) return tests[0];
        if (tests.Count == 2)
        {
            var (a, b) = (tests[0], tests[1]);
            return link => a(link) && b(link);
        }
        var all = tests.ToArray();
        return link =>
        {
            foreach (var t in all)
                if (!t(link)) return false;
            return true;
        };
    }

    /// <summary>
    /// Whether two condition lists would compile the same. Unchanged strings are the same objects, so this is quick.
    /// Compares every field <see cref="RuleCondition.Clone"/> copies; a new condition field needs adding to both.
    /// </summary>
    private static bool SameConditions(List<RuleCondition>? a, List<RuleCondition>? b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a is null || b is null || a.Count != b.Count) return false;
        for (var i = 0; i < a.Count; i++)
        {
            var (x, y) = (a[i], b[i]);
            if (x.Field != y.Field || x.Join != y.Join || x.MatchKind != y.MatchKind || x.CaseSensitive != y.CaseSensitive ||
                x.Negate != y.Negate || x.Values.Count != y.Values.Count || !SameConditions(x.Conditions, y.Conditions))
                return false;
            for (var v = 0; v < x.Values.Count; v++)
                if (!string.Equals(x.Values[v], y.Values[v], StringComparison.Ordinal))
                    return false;
        }
        return true;
    }

    /// <summary>"Condition 2: x" for a direct error; "Condition 3: Condition 2: x" folds into "Condition 3.2: x".</summary>
    private static string Locate(int number, string error)
    {
        const string prefix = "Condition ";
        if (error.StartsWith(prefix, StringComparison.Ordinal))
            return $"{prefix}{number}.{error[prefix.Length..]}";
        return $"{prefix}{number}: {error}";
    }

    /// <summary>Runs <paramref name="url"/> past every rule and reports what each one did (for the rule tester).</summary>
    public MatchResult Match(Uri url, IReadOnlyList<Rule> rules, SourceApp? source = null)
    {
        var started = Stopwatch.GetTimestamp();
        var evaluations = new RuleEvaluation[rules.Count];
        var index = Evaluate(Input(url, source), rules, evaluations, null);
        for (var i = index + 1; index >= 0 && i < rules.Count; i++)
            evaluations[i] = new(rules[i], RuleEvaluationState.NotReached);
        return new MatchResult(url, source, index >= 0 ? rules[index] : null, index, evaluations, Stopwatch.GetElapsedTime(started));
    }

    /// <summary>The first matching rule and its index, without the per-rule report <see cref="Match"/> builds.</summary>
    public (Rule? Rule, int Index) FindMatch(Uri url, IReadOnlyList<Rule> rules, SourceApp? source = null)
    {
        var index = Evaluate(Input(url, source), rules, null, null);
        return index >= 0 ? (rules[index], index) : (null, -1);
    }

    /// <summary>Copies <paramref name="rules"/> for the fastest matching, as long as they don't change. See <see cref="PreparedRules"/>.</summary>
    public PreparedRules Prepare(IReadOnlyList<Rule> rules) => new(rules);

    /// <inheritdoc cref="FindMatch(Uri, IReadOnlyList{Rule}, SourceApp?)"/>
    public (Rule? Rule, int Index) FindMatch(Uri url, PreparedRules rules, SourceApp? source = null)
    {
        var index = Evaluate(Input(url, source), rules.Rules, null, rules.Compiled);
        return index >= 0 ? (rules.Rules[index], index) : (null, -1);
    }

    /// <summary>
    /// Index of the first matching rule, or -1. Fills <paramref name="evaluations"/> up to that rule when given.
    /// <paramref name="prepared"/>, when given, holds compiled rules known to be current (see <see cref="PreparedRules"/>).
    /// </summary>
    private int Evaluate(LinkInput link, IReadOnlyList<Rule> rules, RuleEvaluation[]? evaluations, RuleCompileResult?[]? prepared)
    {
        var now = _time.GetUtcNow();
        for (var i = 0; i < rules.Count; i++)
        {
            var rule = rules[i];
            RuleEvaluation outcome;
            var compiled = !rule.IsActive(now, boot) ? null : prepared is null ? Compile(rule) : prepared[i] ??= Compile(rule);
            if (compiled is null)
            {
                outcome = new(rule, RuleEvaluationState.Inactive);
            }
            else if (compiled.Rule is null)
            {
                outcome = new(rule, RuleEvaluationState.Error, compiled.Error);
            }
            else
            {
                try
                {
                    if (compiled.Rule.IsMatch(link))
                    {
                        if (evaluations is not null) evaluations[i] = new(rule, RuleEvaluationState.Matched);
                        return i;
                    }
                    outcome = new(rule, RuleEvaluationState.NoMatch);
                }
                catch (RegexMatchTimeoutException)
                {
                    outcome = new(rule, RuleEvaluationState.Error, "Match timed out and was skipped.");
                }
            }
            if (evaluations is not null) evaluations[i] = outcome;
        }
        return -1;
    }

    private LinkInput Input(Uri url, SourceApp? source) => new(url, source, _time.GetLocalNow().DateTime);

    private static int Count(RuleCondition c) => c.Values.Count(v => !string.IsNullOrWhiteSpace(v));
}
