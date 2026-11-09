namespace OpenBudget.Connectors;

public enum StepStatus
{
    Done,
    Challenge,
    Failed,
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
