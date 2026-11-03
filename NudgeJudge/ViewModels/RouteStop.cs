namespace NudgeJudge.ViewModels;

public enum RouteStopState
{
    /// <summary>Not evaluated: an earlier rule already matched.</summary>
    Ahead,
    /// <summary>Evaluated and didn't match; the link ran past.</summary>
    Passed,
    /// <summary>Disabled or expired.</summary>
    Inactive,
    Matched,
    Error,
}

/// <summary>One rule's junction on the route diagram.</summary>
public sealed record RouteStop(string Label, RouteStopState State);
