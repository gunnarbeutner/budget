using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenBudget.Migrations;

/// <inheritdoc />
public partial class _20261124050000_ReceiptItemsOwnCategories : Migration
{
    /// <inheritdoc />
    /// <remarks>
    /// Receipt items no longer inherit their booking's category: a stored item category is the item's own. Items that
    /// showed the booking's category (stored without one) get it written out, so receipts keep what they show; products
    /// that fell through to a booking without a category become undecided, so memory, keywords and the AI place them.
    /// </remarks>
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            UPDATE receipt_items SET CategoryId = (
                SELECT t.category_key FROM receipt_invoices r JOIN transactions t ON t.Id = r.TransactionId
                WHERE r.Id = receipt_items.ReceiptInvoiceId)
            WHERE CategoryId IS NULL AND Kind = 0 AND CategorySource IS NOT NULL
            """);
        migrationBuilder.Sql("""
            UPDATE receipt_items SET CategorySource = NULL
            WHERE CategoryId IS NULL AND Kind = 0 AND CategorySource = 'default'
            """);
        migrationBuilder.Sql("UPDATE receipt_items SET CategoryId = NULL WHERE Kind <> 0 AND CategorySource <> 'manual'");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
    }
}
