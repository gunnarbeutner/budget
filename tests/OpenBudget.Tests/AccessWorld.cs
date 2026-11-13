using OpenBudget.Models;
using OpenBudget.Services;

namespace OpenBudget.Tests;

/// <summary>
/// A two-user world mirroring prod: user "alice" (id space 2) owns four projects (Shared, a private
/// one, and a property sub-project of Shared), user "bob" (id space 3) one project. Each project
/// has its own account, and every account has one March-2026 expense in the same tracked category.
/// </summary>
internal static class AccessWorld
{
    public const string Alice = "u-alice";
    public const string Bob = "u-bob";

    public const int SourceAlice = 2;
    public const int SourceBob = 3;

    public const int Shared = 1;
    public const int Private = 2;
    public const int Property = 3;      // sub-project of Shared
    public const int BobProject = 4;

    public static long Id(int source, int row) => RowIds.Namespace(source, row);

    public static long SharedGroup => Id(SourceAlice, 100);
    public static long SharedLeaf => Id(SourceAlice, 200);
    public static long AccountWp => Id(SourceAlice, 1);
    public static long AccountPrivate => Id(SourceAlice, 2);
    public static long AccountProperty => Id(SourceAlice, 3);
    public static long AccountBob => Id(SourceBob, 1);

    public const long SharedCents = -10000;        // 100.00 on the Shared account
    public const long PrivateCents = -50000;   // 500.00 on the private account
    public const long PropertyCents = -2500;   // 25.00 on the property account

    public static async Task<TestDb> BuildAsync(ProjectRole? bobOnWp = ProjectRole.Editor)
    {
        var t = new TestDb();
        t.AddUser(Alice, "alice", admin: true);
        t.AddUser(Bob, "bob");
        t.AddIdSpace(Alice, SourceAlice);
        t.AddIdSpace(Bob, SourceBob);

        foreach (var (owner, source, group, leaf) in new[] { (Alice, SourceAlice, SharedGroup, SharedLeaf), (Bob, SourceBob, Id(SourceBob, 100), Id(SourceBob, 200)) })
        {
            t.Db.CategoryGroups.Add(new CategoryGroup { Id = group, OwnerUserId = owner, Name = $"Group{source}" });
            t.Db.Categories.Add(new Category { Id = leaf, OwnerUserId = owner, Name = $"Leaf{source}", GroupId = group, Type = 1 });
        }

        t.AddAccount(AccountWp, "Giro Shared", Alice);
        t.AddAccount(AccountPrivate, "Giro Private", Alice);
        t.AddAccount(AccountProperty, "Property Account", Alice);
        t.AddAccount(AccountBob, "Bob Giro", Bob);

        var shared = t.AddProject(Shared, "Shared", Alice); shared.OwnerUserId = Alice;
        var priv = t.AddProject(Private, "Alice Private", Alice); priv.OwnerUserId = Alice;
        var prop = t.AddProject(Property, "Property", Alice); prop.OwnerUserId = Alice; prop.ParentProjectId = Shared;
        var bob = t.AddProject(BobProject, "Bob Private", Bob); bob.OwnerUserId = Bob;

        foreach (var p in new[] { Shared, Private, Property })
        {
            t.TrackGroup(p, SharedGroup);
            t.AssignGroup(p, SharedGroup);
        }
        t.TrackGroup(BobProject, Id(SourceBob, 100));
        t.AssignGroup(BobProject, Id(SourceBob, 100));

        t.Db.ProjectAccountAssignments.Add(new ProjectAccountAssignment { ProjectId = Shared, AccountId = AccountWp, Scope = AccountScope.Exclusive });
        t.Db.ProjectAccountAssignments.Add(new ProjectAccountAssignment { ProjectId = Private, AccountId = AccountPrivate, Scope = AccountScope.Exclusive });
        t.Db.ProjectAccountAssignments.Add(new ProjectAccountAssignment { ProjectId = Property, AccountId = AccountProperty, Scope = AccountScope.Exclusive });
        t.Db.ProjectAccountAssignments.Add(new ProjectAccountAssignment { ProjectId = BobProject, AccountId = AccountBob, Scope = AccountScope.Exclusive });

        var march = new DateTime(2026, 3, 10, 0, 0, 0, DateTimeKind.Utc);
        t.AddTransaction(1, SharedLeaf, SharedCents, march, Alice, AccountWp, "Payee Shared");
        t.AddTransaction(2, SharedLeaf, PrivateCents, march, Alice, AccountPrivate, "Payee Private");
        t.AddTransaction(3, SharedLeaf, PropertyCents, march, Alice, AccountProperty, "Payee Property");
        t.AddTransaction(4, Id(SourceBob, 200), -700, march, Bob, AccountBob, "Payee Bob");

        t.AddBudget(Shared, 2026, (SharedGroup, CategoryItemType.Group, 100m));
        t.AddBudget(Private, 2026, (SharedGroup, CategoryItemType.Group, 100m));

        if (bobOnWp is { } role)
            t.Db.ProjectShares.Add(new ProjectShare { ProjectId = Shared, UserId = Bob, Role = role });

        await t.Db.SaveChangesAsync();
        return t;
    }
}
