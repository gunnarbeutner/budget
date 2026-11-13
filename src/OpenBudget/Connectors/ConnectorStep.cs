namespace OpenBudget.Connectors;

public enum StepStatus
{
    Done,
    Challenge,
    Failed,

    /// <summary>A background (silent) run stopped before a step that needs the owner; a manual sync can do it.</summary>
    NeedsUser,
}

/// <summary>Result of one connector call: finished with a value, waiting for the user, or failed.</summary>
public sealed class ConnectorStep<T>
{
    internal ConnectorStep(StepStatus status, T? value, ConnectorChallenge? challenge, string? failureReason, bool credentialsRejected)
    {
        Status = status;
        Value = value;
        Challenge = challenge;
        FailureReason = failureReason;
        CredentialsRejected = credentialsRejected;
    }

    public StepStatus Status { get; }

    public T? Value { get; }

    public ConnectorChallenge? Challenge { get; }

    /// <summary>Safe-to-display reason (never contains credentials).</summary>
    public string? FailureReason { get; }

    /// <summary>The bank refused the username/PIN: retrying without new credentials would only risk a lockout.</summary>
    public bool CredentialsRejected { get; }
}

/// <summary>Factories for <see cref="ConnectorStep{T}"/>.</summary>
public static class ConnectorStep
{
    public static ConnectorStep<T> Done<T>(T value) => new(StepStatus.Done, value, null, null, false);

    public static ConnectorStep<T> Ask<T>(ConnectorChallenge challenge) => new(StepStatus.Challenge, default, challenge, null, false);

    public static ConnectorStep<T> Fail<T>(string reason, bool credentialsRejected = false) =>
        new(StepStatus.Failed, default, null, reason, credentialsRejected);

    /// <summary>A silent run cannot go on without the owner; <paramref name="reason"/> is shown next to the login.</summary>
    public static ConnectorStep<T> NeedsUser<T>(string reason) => new(StepStatus.NeedsUser, default, null, reason, false);

    /// <summary>Why a silent run stopped at <paramref name="challenge"/>, in words for the owner.</summary>
    public static string SilentReason(ConnectorChallenge challenge) => challenge.Kind switch
    {
        ChallengeKind.Approval => "The bank wants an approval in its app.",
        ChallengeKind.Choice => "The bank wants a choice from you.",
        ChallengeKind.WebLogin => "The bank wants you to sign in on its page.",
        ChallengeKind.Code or ChallengeKind.Image => "The bank wants a TAN or code.",
        _ => "The bank wants something from you.",
    };
}

/// <summary>
/// Thrown in a silent (background) run where the next step would need the owner (a TAN, an app approval, a choice) or
/// could notify them. Nothing further is sent to the bank; the run ends as <see cref="StepStatus.NeedsUser"/>.
/// </summary>
public sealed class SilentAbortException : Exception
{
    public SilentAbortException()
    {
    }

    public SilentAbortException(string message) : base(message)
    {
    }

    public SilentAbortException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

/// <summary>
/// Thrown by a connector for an expected, user-presentable failure (bank unavailable, unsupported
/// account). The message is shown to the owner, so it must never contain credentials.
/// </summary>
public sealed class ConnectorException : Exception
{
    public ConnectorException()
    {
    }

    public ConnectorException(string message) : base(message)
    {
    }

    public ConnectorException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
