using OpenBudget.Services;
using Xunit;

namespace OpenBudget.Tests;

/// <summary>
/// The upload/delete endpoints gate on a bearer token. These pin the parsing + the
/// reject-when-disabled/malformed rules; the constant-time guarantee itself isn't observable
/// from a unit test, but the accept/reject behavior is.
/// </summary>
public class UploadAuthTests
{
    [Fact]
    public void Accepts_matching_bearer_token()
    {
        Assert.True(UploadAuth.IsAuthorized("Bearer s3cret", "s3cret"));
    }

    [Fact]
    public void Rejects_wrong_token()
    {
        Assert.False(UploadAuth.IsAuthorized("Bearer nope", "s3cret"));
    }

    [Fact]
    public void Rejects_token_that_is_a_prefix_of_the_expected()
    {
        // FixedTimeEquals fails on length mismatch — a correct prefix must not pass.
        Assert.False(UploadAuth.IsAuthorized("Bearer s3cr", "s3cret"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("s3cret")]            // missing "Bearer " scheme
    [InlineData("bearer s3cret")]     // wrong-case scheme
    [InlineData("Bearer  s3cret")]    // extra space becomes part of the token
    [InlineData("Basic s3cret")]
    public void Rejects_missing_or_malformed_header(string? header)
    {
        Assert.False(UploadAuth.IsAuthorized(header, "s3cret"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Rejects_when_no_token_is_configured(string? expected)
    {
        // Endpoint is disabled when the env var is unset — never authorize.
        Assert.False(UploadAuth.IsAuthorized("Bearer anything", expected));
    }
}
