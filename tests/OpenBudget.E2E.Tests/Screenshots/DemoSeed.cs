using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenBudget.Connectors;
using OpenBudget.Connectors.Sync;
using OpenBudget.Models;
using OpenBudget.Services;

// Random only varies the made-up demo amounts (fixed seed, reproducible), nothing security-related.
#pragma warning disable CA5394

namespace OpenBudget.E2E.Tests.Screenshots;

/// <summary>
/// A fictional household for the README screenshots: three projects (Household, Shared flat, Rental flat), giro,
/// credit card, savings and a depot, May–early November 2026 of bookings with rules, a split and transfers, a 2026
/// budget with some categories over plan, and a few generated statement PDFs. Every name, IBAN and ISIN is made up.
/// Deterministic (fixed random seed) so re-running produces the same pictures.
/// </summary>
internal static class DemoSeed
{
    public const string UserName = "demo";
    public const string Password = "Demo-Passw0rd!";
    public const int SourceId = 1;
    public const int HouseholdId = 1, SharedFlatId = 2, RentalFlatId = 3;
    public const int Year = 2026;

    public static readonly long GiroId = SourceScope.Namespace(SourceId, 1);
    public static readonly long CardId = SourceScope.Namespace(SourceId, 2);
    public static readonly long SavingsId = SourceScope.Namespace(SourceId, 3);
    public static readonly long DepotId = SourceScope.Namespace(SourceId, 4);
    public static readonly long SharedId = SourceScope.Namespace(SourceId, 5);
    public static readonly long RentalId = SourceScope.Namespace(SourceId, 6);

    // Category ids (leaf categories) and groups.
    private const long GLiving = 100, GHome = 101, GMobility = 102, GLeisure = 103, GIncome = 104, GShared = 105, GRental = 106, GTransfers = 107;
    public const long Groceries = 200, Drugstore = 201, Restaurants = 202, Clothing = 203;
    public const long Rent = 210, Energy = 211, Internet = 212, Insurance = 213, Furnishing = 214;
    public const long Transit = 220, Fuel = 221;
    public const long Sports = 230, Streaming = 231, Travel = 232;
    public const long Salary = 240, Interest = 241;
    public const long FlatRent = 250, FlatShares = 251, FlatSupplies = 252, FlatCleaning = 253;
    public const long RentalIncome = 260, Maintenance = 261, PropertyMgmt = 262, PropertyTax = 263;
    public const long OwnTransfer = 270;

    private static readonly DateTime Created = new(2026, 1, 2, 9, 0, 0, DateTimeKind.Utc);

    public static async Task SeedAsync(IServiceProvider services)
    {
        var factory = services.GetRequiredService<SystemDbContextFactory>();
        await E2ESeed.ResetAsync(factory);

        string owner;
        await using (var scope = services.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
            var user = new AppUser { UserName = UserName };
            var result = await users.CreateAsync(user, Password);
            if (!result.Succeeded) throw new InvalidOperationException(string.Join("; ", result.Errors.Select(e => e.Description)));
            owner = user.Id;
        }

        await using var db = await factory.CreateDbContextAsync();
        db.ImportSources.Add(new ImportSource { Id = SourceId, Name = "Household data", CreatedAt = Created, OwnerUserId = owner, UploadToken = "demo-token", LastUploadAt = new DateTime(2026, 11, 10, 6, 0, 0, DateTimeKind.Utc) });

        // ---- categories ----
        Group(db, GLiving, "Living", "#4e9a06");
        Group(db, GHome, "Home", "#3465a4");
        Group(db, GMobility, "Mobility", "#c17d11");
        Group(db, GLeisure, "Leisure", "#75507b");
        Group(db, GIncome, "Income", "#2e7d32");
        Group(db, GShared, "Shared flat", "#06989a");
        Group(db, GRental, "Rental flat", "#a40000");
        Group(db, GTransfers, "Transfers", "#888a85");
        Cat(db, Groceries, "Groceries", GLiving); Cat(db, Drugstore, "Drugstore", GLiving); Cat(db, Restaurants, "Restaurants", GLiving); Cat(db, Clothing, "Clothing", GLiving);
        Cat(db, Rent, "Rent", GHome); Cat(db, Energy, "Energy", GHome); Cat(db, Internet, "Internet & phone", GHome); Cat(db, Insurance, "Insurance", GHome); Cat(db, Furnishing, "Furnishing", GHome);
        Cat(db, Transit, "Public transport", GMobility); Cat(db, Fuel, "Car sharing", GMobility);
        Cat(db, Sports, "Sports", GLeisure); Cat(db, Streaming, "Streaming", GLeisure); Cat(db, Travel, "Travel", GLeisure);
        Cat(db, Salary, "Salary", GIncome); Cat(db, Interest, "Interest & dividends", GIncome);
        Cat(db, FlatRent, "Flat rent", GShared); Cat(db, FlatShares, "Flatmate shares", GShared); Cat(db, FlatSupplies, "Shared supplies", GShared); Cat(db, FlatCleaning, "Cleaning", GShared);
        Cat(db, RentalIncome, "Rental income", GRental); Cat(db, Maintenance, "Maintenance", GRental); Cat(db, PropertyMgmt, "Property management", GRental); Cat(db, PropertyTax, "Property tax", GRental);
        Cat(db, OwnTransfer, "Own transfer", GTransfers);

        // ---- accounts ----
        db.AccountGroups.Add(new AccountGroup { Id = 1, SourceId = SourceId, Name = "Example Bank", Sort = 1 });
        db.AccountGroups.Add(new AccountGroup { Id = 2, SourceId = SourceId, Name = "Savings & investments", Sort = 2 });
        db.AccountGroups.Add(new AccountGroup { Id = 3, SourceId = SourceId, Name = "Flats", Sort = 3 });
        Acct(db, GiroId, "Giro account", 1, 1, "DE02 1001 0010 0000 0000 01", 3_412.18m, 1);
        Acct(db, CardId, "Credit card", 3, 1, null, -386.40m, 2);
        Acct(db, SavingsId, "Daily savings", 2, 2, "DE02 1001 0010 0000 0000 02", 14_250.00m, 3);
        db.Accounts.Add(new Account { Id = DepotId, SourceId = SourceId, Name = "Example Depot", Type = 7, Currency = "EUR", GroupId = 2, Sort = 4, Provider = "fake" });
        Acct(db, SharedId, "Shared flat account", 1, 3, "DE02 1001 0010 0000 0000 03", 1_204.55m, 5);
        Acct(db, RentalId, "Rental flat account", 1, 3, "DE02 1001 0010 0000 0000 04", 6_870.20m, 6);
        await db.SaveChangesAsync();

        Holding(db, 1, "Example World Equity ETF", "XX0000000011", 142, 98.42, 81.10);
        Holding(db, 2, "Sample Emerging Markets ETF", "XX0000000029", 210, 31.87, 29.40);
        Holding(db, 3, "Example Euro Government Bond ETF", "XX0000000037", 60, 118.05, 121.30);
        Holding(db, 4, "Sample Industrial Holdings AG", "XX0000000045", 35, 64.20, 52.75);
        Holding(db, 5, "Example Clean Energy ETF", "XX0000000052", 80, 12.36, 15.90);

        // ---- projects ----
        Project(db, owner, HouseholdId, "Household", "Everyday money: giro, credit card, savings and the depot", 0,
            [GiroId, CardId, SavingsId, DepotId], [GLiving, GHome, GMobility, GLeisure, GIncome], [Salary, Interest]);
        Project(db, owner, SharedFlatId, "Shared flat", "The flat-share account with two flatmates", 1,
            [SharedId], [GShared], [FlatShares]);
        Project(db, owner, RentalFlatId, "Rental flat", "The small flat that is let out", 2,
            [RentalId], [GRental], [RentalIncome]);
        await db.SaveChangesAsync();

        // ---- budgets ----
        await BudgetAsync(db, HouseholdId, new()
        {
            [Groceries] = 420,
            [Drugstore] = 45,
            [Restaurants] = 110,
            [Clothing] = 80,
            [Rent] = 1_250,
            [Energy] = 95,
            [Internet] = 40,
            [Insurance] = 38,
            [Furnishing] = 60,
            [Transit] = 58,
            [Fuel] = 40,
            [Sports] = 35,
            [Streaming] = 25,
            [Travel] = 150,
            [Salary] = 3_850,
            [Interest] = 25,
        });
        await BudgetAsync(db, SharedFlatId, new() { [FlatRent] = 1_380, [FlatShares] = 920, [FlatSupplies] = 60, [FlatCleaning] = 45 });
        await BudgetAsync(db, RentalFlatId, new() { [RentalIncome] = 780, [Maintenance] = 80, [PropertyMgmt] = 42, [PropertyTax] = 25 });

        // ---- rules ----
        var marketRule = new CategoryRule { ProjectId = HouseholdId, SortOrder = 0, Name = "Example Market", Expression = "name:\"Example Market\" AND amount<0", CategoryId = Groceries, CreatedAt = Created };
        var energyRule = new CategoryRule { ProjectId = HouseholdId, SortOrder = 1, Name = "Sample Energy", Expression = "name:\"Sample Energy\" AND amount<0", CategoryId = Energy, CreatedAt = Created };
        var transitRule = new CategoryRule { ProjectId = HouseholdId, SortOrder = 2, Name = "Transit pass", Expression = "purpose:\"monthly pass\" OR booking_text:\"monthly pass\"", CategoryId = Transit, CreatedAt = Created };
        db.CategoryRules.AddRange(marketRule, energyRule, transitRule);
        await db.SaveChangesAsync();

        // ---- bookings ----
        var b = new Bookings(db);
        var rnd = new Random(20260501);
        string[] markets = ["Example Market", "Fresh Corner", "Sample Organic Store"];
        for (var month = 5; month <= 11; month++)
        {
            var last = month == 11 ? 9 : DateTime.DaysInMonth(Year, month);
            bool Has(int day) => day <= last;
            var m = month;
            DateTime D(int day) => new(Year, m, Math.Min(day, last), 0, 0, 0, DateTimeKind.Utc);

            // Giro: fixed costs and salary.
            if (Has(1)) b.Add(GiroId, D(1), -1_250_00, "Example Housing eG", Rent, $"Rent {CultureInfo.InvariantCulture.DateTimeFormat.GetMonthName(m)} flat 3B", "Dauerauftrag");
            if (Has(3)) b.Add(GiroId, D(3), -95_00, "Sample Energy GmbH", Energy, $"Advance payment electricity customer 4711-0815", "Lastschrift", rule: energyRule);
            if (Has(5)) b.Add(GiroId, D(5), -39_99, "Example Telecom", Internet, "Fibre 250 + mobile", "Lastschrift");
            if (Has(6)) b.Add(GiroId, D(6), -58_00, "City Transit Example", Transit, "Germany monthly pass", "Lastschrift", rule: transitRule);
            if (Has(8)) b.Add(GiroId, D(8), -34_90, "Sample Fitness Club", Sports, "Membership", "Lastschrift");
            if (Has(15)) b.Add(GiroId, D(15), -37_80, "Example Insurance AG", Insurance, "Household & liability insurance", "Lastschrift");
            if (Has(25)) b.Add(GiroId, D(25), 3_850_00, "Sample Software AG", Salary, $"Salary {m:D2}/2026", "Gehalt");
            if (Has(30)) b.Add(SavingsId, D(30), 1_87, "Example Bank", Interest, "Interest", "Zinsen");

            // Transfers: savings on the 26th, credit card settlement on the 2nd.
            if (Has(26)) b.Transfer(GiroId, SavingsId, D(26), 400_00, "Daily savings", "Monthly savings");
            if (Has(2) && m > 5) b.Transfer(GiroId, CardId, D(2), 300_00 + rnd.Next(0, 200_00), "Credit card settlement", "Card statement");

            // Groceries and drugstore: a handful per month, slightly over plan in summer.
            var shops = m is 7 or 8 or 10 ? 10 : 8;
            for (var i = 0; i < shops; i++)
            {
                var day = 1 + i * (last - 1) / shops + rnd.Next(0, 3);
                if (!Has(day)) continue;
                var shop = markets[i % markets.Length];
                b.Add(i % 2 == 0 ? GiroId : CardId, D(day), -(rnd.Next(18_00, 78_00)), shop, Groceries, $"Card payment {shop}", "Kartenzahlung",
                    rule: shop == "Example Market" ? marketRule : null, isNew: m == 11);
            }
            if (Has(12)) b.Add(GiroId, D(12), -rnd.Next(14_00, 38_00), "Example Drugstore", Drugstore, "Card payment", "Kartenzahlung");

            // Credit card: restaurants, streaming, car sharing.
            if (Has(4)) b.Add(CardId, D(4), -12_99, "Streamly Video", Streaming, "Subscription", "Kartenzahlung");
            if (Has(9)) b.Add(CardId, D(9), -9_99, "Sample Music", Streaming, "Family plan", "Kartenzahlung");
            for (var i = 0; i < 4; i++)
            {
                var day = 3 + i * 7 + rnd.Next(0, 3);
                if (Has(day)) b.Add(CardId, D(day), -rnd.Next(18_00, 46_00), i % 2 == 0 ? "Trattoria Esempio" : "Sample Noodle Bar", Restaurants, "Card payment", "Kartenzahlung");
            }
            if (Has(19)) b.Add(CardId, D(19), -rnd.Next(18_00, 52_00), "Example Car Sharing", Fuel, "Trips", "Kartenzahlung");

            // Shared flat: flatmate shares in, rent and supplies out.
            if (Has(1)) b.Add(SharedId, D(1), 460_00, "Alex Example", FlatShares, "Share rent + supplies", "Gutschrift");
            if (Has(1)) b.Add(SharedId, D(1), 460_00, "Sam Sample", FlatShares, "Share rent + supplies", "Gutschrift");
            if (Has(2)) b.Add(SharedId, D(2), -1_380_00, "Sample Living GmbH", FlatRent, "Rent shared flat 2nd floor left", "Dauerauftrag");
            if (Has(14)) b.Add(SharedId, D(14), -rnd.Next(28_00, 76_00), "Example Market", FlatSupplies, "Cleaning agents, paper", "Kartenzahlung");
            if (Has(20)) b.Add(SharedId, D(20), -45_00, "Sparkle Cleaning Example", FlatCleaning, "Stairwell cleaning", "Lastschrift");

            // Rental flat.
            if (Has(3)) b.Add(RentalId, D(3), 780_00, "Jordan Tenant", RentalIncome, "Rent incl. service charges", "Gutschrift");
            if (Has(10)) b.Add(RentalId, D(10), -42_00, "Example Property Services", PropertyMgmt, "Management fee", "Lastschrift");
            if (m is 5 or 8 && Has(15)) b.Add(RentalId, D(15), -74_50, "Example Town Treasury", PropertyTax, "Property tax instalment", "Lastschrift");
        }

        // One-offs: a summer trip that blows the travel budget, a boiler repair, a sofa, clothing.
        b.Add(CardId, new DateTime(2026, 7, 18, 0, 0, 0, DateTimeKind.Utc), -642_00, "Example Airlines", Travel, "Flights 2 persons", "Kartenzahlung");
        b.Add(CardId, new DateTime(2026, 8, 2, 0, 0, 0, DateTimeKind.Utc), -486_30, "Seaside Sample Hotel", Travel, "4 nights", "Kartenzahlung");
        b.Add(RentalId, new DateTime(2026, 9, 22, 0, 0, 0, DateTimeKind.Utc), -318_00, "Example Plumbing", Maintenance, "Boiler service and repair", "Überweisung");
        b.Add(GiroId, new DateTime(2026, 6, 13, 0, 0, 0, DateTimeKind.Utc), -899_00, "Sample Furniture House", Furnishing, "Sofa \"Linnea\"", "Kartenzahlung");
        b.Add(CardId, new DateTime(2026, 10, 11, 0, 0, 0, DateTimeKind.Utc), -129_95, "Example Outdoor Wear", Clothing, "Rain jacket", "Kartenzahlung");
        var splitId = b.Add(GiroId, new DateTime(2026, 11, 9, 0, 0, 0, DateTimeKind.Utc), -184_30, "Example Department Store", null, "Card payment", "Kartenzahlung");
        b.Add(GiroId, new DateTime(2026, 10, 21, 0, 0, 0, DateTimeKind.Utc), -23_40, "Sample Bakery", null, "Card payment", "Kartenzahlung", isNew: true);
        b.Add(GiroId, new DateTime(2026, 11, 7, 0, 0, 0, DateTimeKind.Utc), -64_90, "Example Hardware Store", null, "Card payment", "Kartenzahlung", isNew: true);
        await db.SaveChangesAsync();

        // The department-store receipt is split through the real split logic (groceries, clothing, furnishing).
        await using (var scope = services.CreateAsyncScope())
        {
            // Built over the unrestricted factory: the app's own one only sees the signed-in user's data.
            var split = new SplitService(factory, scope.ServiceProvider.GetRequiredService<ChangeNotificationService>(), scope.ServiceProvider.GetRequiredService<ChangeOrigin>());
            await split.SplitAsync(splitId,
            [
                new SplitPartInput(-62_30, Groceries, "Food"),
                new SplitPartInput(-79_00, Clothing, "Shoes"),
                new SplitPartInput(-43_00, Furnishing, "Bed linen"),
            ]);
        }

        await SeedStatementsAsync(services, db, owner);
    }

    // ---- helpers ----

    private static void Group(BudgetDbContext db, long id, string name, string color) =>
        db.CategoryGroups.Add(new CategoryGroup { Id = id, SourceId = SourceId, Name = name, Color = color, Sort = id });

    private static void Cat(BudgetDbContext db, long id, string name, long group) =>
        db.Categories.Add(new Category { Id = id, SourceId = SourceId, Name = name, GroupId = group, Type = 1, Sort = id });

    private static void Acct(BudgetDbContext db, long id, string name, long type, long group, string? iban, decimal balance, long sort) =>
        db.Accounts.Add(new Account
        {
            Id = id,
            SourceId = SourceId,
            Name = name,
            Type = type,
            GroupId = group,
            Iban = iban,
            Sort = sort,
            Provider = "fake",
            Currency = "EUR",
            Balances = $"11:{balance.ToString("0.000000", CultureInfo.InvariantCulture)},3:EUR,",
            RefreshTimestamp = new DateTimeOffset(2026, 11, 10, 7, 30, 0, TimeSpan.Zero).ToUnixTimeSeconds(),
        });

    private static void Holding(BudgetDbContext db, long id, string name, string isin, double qty, double price, double purchase) =>
        db.Holdings.Add(new Holding
        {
            Id = id,
            SourceId = SourceId,
            AccountId = DepotId,
            Name = name,
            Isin = isin,
            Quantity = qty,
            Price = price,
            PriceCurrency = "EUR",
            Amount = Math.Round(qty * price, 2),
            AmountCurrency = "EUR",
            Purchase = purchase,
        });

    private static void Project(BudgetDbContext db, string owner, int id, string name, string description, int sort, long[] accounts, long[] groups, long[] income)
    {
        db.Projects.Add(new Project { Id = id, Name = name, Description = description, SourceId = SourceId, OwnerUserId = owner, SortOrder = sort, CreatedAt = Created, IsDefaultForNewAccounts = id == HouseholdId });
        foreach (var a in accounts) db.ProjectAccountAssignments.Add(new ProjectAccountAssignment { ProjectId = id, AccountId = a });
        foreach (var g in groups)
        {
            db.ProjectCategoryAssignments.Add(new ProjectCategoryAssignment { ProjectId = id, CategoryId = g });
            db.ProjectTrackingItems.Add(new ProjectTrackingItem { ProjectId = id, CategoryGroupId = g });
        }
        foreach (var c in db.ChangeTracker.Entries<Category>().Select(e => e.Entity).Where(c => groups.Contains(c.GroupId ?? 0)))
            db.ProjectTrackingItems.Add(new ProjectTrackingItem { ProjectId = id, CategoryId = c.Id });
        foreach (var c in income)
            db.CategoryTypeAssignments.Add(new CategoryTypeAssignment { ProjectId = id, CategoryId = c, ItemType = CategoryItemType.Category, IsIncome = true });
    }

    private static async Task BudgetAsync(BudgetDbContext db, int projectId, Dictionary<long, decimal> monthly)
    {
        var budget = new Budget { ProjectId = projectId, Year = Year, Name = $"{Year}", IsActive = true, CreatedAt = Created };
        db.Budgets.Add(budget);
        await db.SaveChangesAsync();
        foreach (var (category, amount) in monthly)
        {
            var entry = new MonthlyBudgetEntry { BudgetId = budget.Id, CategoryId = category, ItemType = CategoryItemType.Category };
            for (var m = 1; m <= 12; m++) entry.SetMonthAmount(m, amount);
            db.MonthlyBudgetEntries.Add(entry);
        }
        await db.SaveChangesAsync();
    }

    private sealed class Bookings(BudgetDbContext db)
    {
        private long _next = 1000;

        public long Add(long account, DateTime date, long cents, string payee, long? category, string purpose, string bookingText,
            CategoryRule? rule = null, bool isNew = false)
        {
            var id = _next++;
            db.Transactions.Add(new Transaction
            {
                Id = id,
                SourceId = SourceId,
                AccountId = account,
                Amount = cents,
                Currency = "EUR",
                Name = payee,
                Purpose = purpose,
                CategoryId = category,
                Timestamp = new DateTimeOffset(date).ToUnixTimeSeconds(),
                ValueTimestamp = new DateTimeOffset(date.AddDays(1)).ToUnixTimeSeconds(),
                BookingText = bookingText,
                BankReference = $"EX{id:D8}",
                ExternalId = $"demo-{id}",
                CounterpartyIban = cents > 0 && category == Salary ? "DE02 3006 0601 0000 0000 99" : null,
                CategorySetBy = rule != null ? TransactionCategorySource.Rule : TransactionCategorySource.Import,
                RuleId = rule?.Id,
                IsNew = isNew,
            });
            return id;
        }

        public void Transfer(long from, long to, DateTime date, long cents, string name, string purpose)
        {
            Add(from, date, -cents, name, OwnTransfer, purpose, "Überweisung");
            Add(to, date, cents, "Giro account", OwnTransfer, purpose, "Gutschrift");
        }
    }

    // ---- statements ----

    private static async Task SeedStatementsAsync(IServiceProvider services, BudgetDbContext db, string owner)
    {
        var login = new BankLogin { OwnerUserId = owner, ConnectorType = "fake", DisplayName = "Example Bank", RefreshInterval = RefreshInterval.ManualOnly, LastSyncAt = new DateTime(2026, 11, 10, 7, 30, 0, DateTimeKind.Utc) };
        db.BankLogins.Add(login);
        await db.SaveChangesAsync();
        db.BankLoginAccounts.Add(new BankLoginAccount { BankLoginId = login.Id, RemoteId = "DEMO-GIRO", Name = "Giro account", LinkedAccountId = GiroId, StatementsEnabled = true, StatementsInterval = StatementsInterval.ManualOnly, LastSuccessAt = login.LastSyncAt });
        db.BankLoginAccounts.Add(new BankLoginAccount { BankLoginId = login.Id, RemoteId = "DEMO-CARD", Name = "Credit card", LinkedAccountId = CardId, StatementsEnabled = true, StatementsInterval = StatementsInterval.ManualOnly, LastSuccessAt = login.LastSyncAt });
        await db.SaveChangesAsync();

        var store = services.GetRequiredService<BankStatementStore>();
        var giro = await db.Accounts.SingleAsync(a => a.Id == GiroId);
        var card = await db.Accounts.SingleAsync(a => a.Id == CardId);
        var rows = await db.Transactions.AsNoTracking().Where(t => t.AccountId == GiroId || t.AccountId == CardId).ToListAsync();
        for (var m = 5; m <= 10; m++)
        {
            var date = new DateOnly(Year, m, DateTime.DaysInMonth(Year, m));
            var month = m;
            List<Transaction> Of(long acct) => rows.Where(t => t.AccountId == acct && t.Date.Month == month && t.Splitted != 2).OrderBy(t => t.Timestamp).ToList();
            await store.StoreAsync(db, login, giro, new RemoteStatement($"G-{m}", "DEMO-GIRO", date, $"Account statement {m:D2}/2026",
                StatementPdf("Account statement", "Giro account  DE02 1001 0010 0000 0000 01", date, Of(GiroId))), CancellationToken.None);
            await store.StoreAsync(db, login, card, new RemoteStatement($"C-{m}", "DEMO-CARD", date, $"Credit card statement {m:D2}/2026",
                StatementPdf("Credit card statement", "Example Card  •••• 0000", date, Of(CardId))), CancellationToken.None);
        }
        await store.StoreAsync(db, login, null, new RemoteStatement("TAX-2025", null, new DateOnly(2026, 2, 14), "Annual tax certificate 2025",
            StatementPdf("Annual tax certificate 2025", "Example Depot", new DateOnly(2026, 2, 14), [])), CancellationToken.None);
    }

    /// <summary>A one-page A4 PDF that looks like a bank statement: header, account line, booking table.</summary>
    private static byte[] StatementPdf(string title, string account, DateOnly date, List<Transaction> bookings)
    {
        var inv = CultureInfo.InvariantCulture;
        var de = CultureInfo.GetCultureInfo("de-DE");
        static string Esc(string s) => s.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)").Replace("•", "*").Replace("ü", "ue");
        var c = new StringBuilder();
        void Text(string font, int size, double x, double y, string s) => c.Append(inv, $"BT /{font} {size} Tf {x:0} {y:0} Td ({Esc(s)}) Tj ET\n");
        c.Append("0.11 0.33 0.55 rg 0 782 595 60 re f 1 1 1 rg\n");
        Text("F2", 20, 48, 806, "Example Bank");
        c.Append("0 0 0 rg\n");
        Text("F2", 16, 48, 740, title);
        Text("F1", 10, 48, 722, account);
        Text("F1", 10, 400, 740, $"Date: {date.ToString("dd.MM.yyyy", inv)}");
        Text("F1", 10, 400, 722, "Page 1 of 1");
        c.Append("0.6 g 48 700 m 547 700 l S 0 g\n");
        var y = 684.0;
        Text("F2", 9, 48, y, "Date"); Text("F2", 9, 110, y, "Description"); Text("F2", 9, 480, y, "Amount EUR");
        y -= 18;
        foreach (var t in bookings.Take(30))
        {
            Text("F1", 9, 48, y, t.Date.ToString("dd.MM.", inv));
            Text("F1", 9, 110, y, $"{t.Name} - {t.Purpose}");
            Text("F1", 9, 480, y, (t.Amount / 100m).ToString("#,##0.00", de));
            y -= 15;
        }
        if (bookings.Count == 0)
        {
            Text("F1", 10, 48, y, "Capital gains 2025:  1.284,16 EUR");
            Text("F1", 10, 48, y - 16, "Allowance used:      1.000,00 EUR");
            Text("F1", 10, 48, y - 32, "Tax withheld:          75,17 EUR");
        }
        Text("F1", 7, 48, 40, "Example Bank AG - Sample Street 1 - 12345 Example City - this is a fictional demo document");
        var stream = c.ToString();
        string[] objects =
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Contents 4 0 R /Resources << /Font << /F1 5 0 R /F2 6 0 R >> >> >>",
            $"<< /Length {Encoding.ASCII.GetByteCount(stream).ToString(inv)} >>\nstream\n{stream}\nendstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold >>",
        ];
        var sb = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (var i = 0; i < objects.Length; i++)
        {
            offsets.Add(sb.Length);
            sb.Append(inv, $"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }
        var xref = sb.Length;
        sb.Append(inv, $"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (var o in offsets) sb.Append(inv, $"{o:D10} 00000 n \n");
        sb.Append(inv, $"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
