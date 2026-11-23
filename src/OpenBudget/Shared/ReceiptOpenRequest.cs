namespace OpenBudget.Shared;

/// <summary>A receipt to show in the page's receipt panel: who may move its items, and what lines without a category show.</summary>
public sealed record ReceiptOpenRequest(long ReceiptId, bool CanEdit, string? BookingCategoryName);
