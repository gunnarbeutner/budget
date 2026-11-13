using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.EntityFrameworkCore;
using OpenBudget.Models;
using OpenBudget.Services;
using System.Reflection;

namespace OpenBudget.Shared;

/// <summary>
/// Minimum project role a page needs. Pages with a <c>{ProjectId}</c> or <c>{BudgetId}</c> route
/// parameter default to <see cref="ProjectRole.Viewer"/>; editing pages declare <see cref="ProjectRole.Editor"/>.
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class RequireProjectRoleAttribute : Attribute
{
    public RequireProjectRoleAttribute(ProjectRole role) => Role = role;

    public ProjectRole Role { get; }
}

/// <summary>
/// The router's route view, with the access check built in: a user who has not finished Get started is sent there
/// (<see cref="GetStartedGate"/>); otherwise, before a page renders, the project / budget named in the URL must be
/// within the current user's scope and role. Otherwise the
/// not-found page is shown (HTTP 404 when prerendering) — identical to a URL that doesn't exist, so
/// the existence of other users' projects is never revealed. Nothing is rendered until the check
/// has completed, so no page code (and no data loading) runs for an out-of-scope URL.
/// </summary>
public sealed class AccessRouteView : ComponentBase
{
    private bool? _allowed;

    [Parameter, EditorRequired]
    public Microsoft.AspNetCore.Components.RouteData RouteData { get; set; } = default!;

    [Parameter]
    public Type? DefaultLayout { get; set; }

    [Inject]
    private AccessScopeProvider Access { get; set; } = default!;

    [Inject]
    private IDbContextFactory<BudgetDbContext> DbFactory { get; set; } = default!;

    [Inject]
    private IHttpContextAccessor HttpContextAccessor { get; set; } = default!;

    [Inject]
    private GetStartedGate GetStarted { get; set; } = default!;

    [Inject]
    private NavigationManager Navigation { get; set; } = default!;

    protected override async Task OnParametersSetAsync()
    {
        _allowed = null;
        // Until the user is through with Get started, it is the only page that opens.
        // (Asked on every page: the layout reads the answer to hide the navigation.)
        if ((await Access.GetAsync()).IsAuthenticated && await GetStarted.IsCaptiveAsync() && RouteData.PageType != typeof(Pages.GetStarted))
        {
            Navigation.NavigateTo("/get-started", replace: true);
            return;
        }
        _allowed = await IsAllowedAsync();
        if (_allowed == false && HttpContextAccessor.HttpContext is { Response.HasStarted: false } http)
            http.Response.StatusCode = StatusCodes.Status404NotFound;
    }

    private async Task<bool> IsAllowedAsync()
    {
        var scope = await Access.GetAsync();
        if (!scope.IsAuthenticated) return false;

        var pageType = RouteData.PageType;
        var values = RouteData.RouteValues;

        if (TryGetInt(values, "ProjectId", out var projectId))
            return scope.RoleFor(projectId) >= RequiredRole(pageType);

        if (TryGetInt(values, "BudgetId", out var budgetId))
        {
            // Resolved through the user-scoped context: a budget outside the scope simply isn't there.
            await using var db = await DbFactory.CreateDbContextAsync();
            var owningProject = await db.Budgets.AsNoTracking()
                .Where(b => b.Id == budgetId)
                .Select(b => (int?)b.ProjectId)
                .FirstOrDefaultAsync();
            return owningProject is { } pid && scope.RoleFor(pid) >= RequiredRole(pageType);
        }

        return true;
    }

    private static ProjectRole RequiredRole(Type? pageType) =>
        pageType?.GetCustomAttribute<RequireProjectRoleAttribute>()?.Role ?? ProjectRole.Viewer;

    private static bool TryGetInt(IReadOnlyDictionary<string, object?> values, string key, out int value)
    {
        if (values.TryGetValue(key, out var raw) && raw is int i)
        {
            value = i;
            return true;
        }
        value = 0;
        return false;
    }

    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        if (_allowed == null) return;

        if (_allowed == true)
        {
            builder.OpenComponent<RouteView>(0);
            builder.AddComponentParameter(1, nameof(RouteView.RouteData), RouteData);
            builder.AddComponentParameter(2, nameof(RouteView.DefaultLayout), DefaultLayout);
            builder.CloseComponent();
            return;
        }

        builder.OpenComponent<LayoutView>(3);
        builder.AddComponentParameter(4, nameof(LayoutView.Layout), DefaultLayout);
        builder.AddAttribute(5, nameof(LayoutView.ChildContent), (RenderFragment)(b =>
        {
            b.OpenComponent<NotFoundMessage>(6);
            b.CloseComponent();
        }));
        builder.CloseComponent();
    }
}
