using Xunit;
using OpenBudget.Connectors;
using OpenBudget.Connectors.Sync;

namespace OpenBudget.Tests.Connectors;

/// <summary>The sync dialog's choice of step while several syncs run (sync all): a question is never displaced.</summary>
public sealed class SyncChallengesTests
{
    private static readonly WebLoginSpec Spec = new(new Uri("https://bank.test/login"), ["bank.test"], new Uri("https://bank.test/home"));

    private static BankSyncStatus Status(int login, ConnectorChallenge? challenge, SyncPhase phase) =>
        new(login, "owner", phase, null, DateTime.UtcNow, null, null, null, challenge, Guid.NewGuid(), null);

    private static BankSyncStatus HiddenWebLogin(int login) => Status(login, ConnectorChallenge.WebLogin("Sign in.", Spec), SyncPhase.Running);

    private static BankSyncStatus Question(int login) => Status(login, ConnectorChallenge.Code("Enter the code."), SyncPhase.WaitingForInput);

    [Fact]
    public void A_web_login_running_by_itself_does_not_take_the_dialog_from_a_question()
    {
        var background = HiddenWebLogin(1);
        var question = Question(2);

        Assert.Same(question, SyncChallenges.Pick([background, question], Guid.Empty, showBrowser: false));
        Assert.Same(question, SyncChallenges.Pick([background, question], question.ChallengeId, showBrowser: false));
    }

    [Fact]
    public void The_question_shown_stays_while_another_one_comes_up()
    {
        var first = Question(1);
        var second = Question(2);

        Assert.Same(second, SyncChallenges.Pick([first, second], second.ChallengeId, showBrowser: false));
    }

    [Fact]
    public void A_web_login_the_user_opened_stays_and_nothing_open_shows_nothing()
    {
        var background = HiddenWebLogin(1);
        var question = Question(2);

        Assert.Same(background, SyncChallenges.Pick([background, question], background.ChallengeId, showBrowser: true));
        Assert.Same(background, SyncChallenges.Pick([background], Guid.Empty, showBrowser: false));
        Assert.Null(SyncChallenges.Pick([Status(3, null, SyncPhase.Running)], Guid.Empty, showBrowser: false));
    }
}
