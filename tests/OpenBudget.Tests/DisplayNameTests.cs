using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenBudget.Auth;
using OpenBudget.Models;
using Xunit;

namespace OpenBudget.Tests;

/// <summary>
/// The optional display name: normalised on save (trimmed, cut to 60, blank = none), edited on the
/// account page, and shown to others in place of the user name when set.
/// </summary>
public sealed class DisplayNameTests : IDisposable
{
    private readonly AppTestHost _host = new();

    public void Dispose() => _host.Dispose();

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("  Test Person  ", "Test Person")]
    public void Normalize_trims_and_treats_blank_as_none(string? input, string? expected) =>
        Assert.Equal(expected, AppUser.NormalizeDisplayName(input));

    [Fact]
    public void Normalize_cuts_to_the_maximum_length()
    {
        var result = AppUser.NormalizeDisplayName(new string('x', 100));
        Assert.Equal(AppUser.DisplayNameMaxLength, result!.Length);
    }

    [Fact]
    public void Shown_name_falls_back_to_the_user_name()
    {
        Assert.Equal("alice", AppUser.ShownName(null, "alice"));
        Assert.Equal("alice", AppUser.ShownName(" ", "alice"));
        Assert.Equal("Test Person", AppUser.ShownName("Test Person", "alice"));
    }

    private static async Task<HttpResponseMessage> SaveAsync(HttpClient client, string value)
    {
        var token = await AppTestHost.FetchAntiforgeryTokenAsync(client, "/Account");
        return await AppTestHost.PostFormAsync(client, "/Account?handler=DisplayName",
            KeyValuePair.Create("displayName", value),
            KeyValuePair.Create("__RequestVerificationToken", token));
    }

    private async Task<string?> StoredAsync(string userName)
    {
        await using var db = await _host.System.CreateDbContextAsync();
        return await db.Users.Where(u => u.UserName == userName).Select(u => u.DisplayName).SingleAsync();
    }

    private async Task<string> ShownAsync(string userId)
    {
        using var scope = _host.Services.CreateScope();
        var names = await scope.ServiceProvider.GetRequiredService<UserDirectory>().GetNamesAsync([userId]);
        return names[userId];
    }

    [Fact]
    public async Task Account_page_saves_a_trimmed_display_name_and_clears_it_when_empty()
    {
        await _host.StartAsync();
        var alice = await _host.AddUserAsync("alice");
        await _host.AddUserAsync("bob");
        using var client = await _host.LoggedInClientAsync("alice");

        using (var saved = await SaveAsync(client, "  Test Person  "))
            Assert.Equal(HttpStatusCode.Redirect, saved.StatusCode);
        Assert.Equal("Test Person", await StoredAsync("alice"));
        Assert.Equal("Test Person", await ShownAsync(alice.Id));
        Assert.Null(await StoredAsync("bob"));

        using (await SaveAsync(client, new string('y', 80)))
            Assert.Equal(new string('y', 60), await StoredAsync("alice"));

        using (await SaveAsync(client, "   "))
            Assert.Null(await StoredAsync("alice"));
        Assert.Equal("alice", await ShownAsync(alice.Id));
    }
}
