using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.EntityFrameworkCore;
using OpenBudget.Services;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorPages();
builder.Services.AddServerSideBlazor();
builder.Services.AddScoped<BudgetService>();

// Configure database paths
var dataDir = Path.Combine(builder.Environment.ContentRootPath, "data");
var moneyMoneyDbPath = Path.Combine(dataDir, "moneymoney.db");
var budgetDbPath = Path.Combine(dataDir, "budgets.db");

// Configure MoneyMoney database (plain SQLite, read-only)
builder.Services.AddDbContext<MoneyMoneyDbContext>(options =>
{
    options.UseSqlite($"Data Source={moneyMoneyDbPath};Mode=ReadOnly", sqliteOptions =>
    {
        sqliteOptions.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery);
    });
}, ServiceLifetime.Scoped);

// Configure Budget database (our data, read-write)
builder.Services.AddDbContext<BudgetDbContext>(options =>
{
    options.UseSqlite($"Data Source={budgetDbPath}");
}, ServiceLifetime.Scoped);

var app = builder.Build();

// Initialize budget database
using (var scope = app.Services.CreateScope())
{
    var budgetDb = scope.ServiceProvider.GetRequiredService<BudgetDbContext>();
    budgetDb.Database.EnsureCreated();
}

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseHttpsRedirection();

app.UseStaticFiles();

app.UseRouting();

app.MapBlazorHub();
app.MapFallbackToPage("/_Host");

app.Run();
