using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenBudget.Migrations
{
    /// <inheritdoc />
    public partial class NormalizeSubcatBudgets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Enforce the new invariant: at any subtree, budget amounts live at one level only.
            // For every group MonthlyBudgetEntry that has a non-zero total AND has any descendant
            // (sub-group or leaf category) with a non-zero entry in the same budget, drop the
            // group entry — the descendants take precedence. Idempotent.
            migrationBuilder.Sql(@"
WITH RECURSIVE
  group_tree(root_id, descendant_group_id) AS (
    SELECT Id, Id FROM mm_category_groups
    UNION ALL
    SELECT t.root_id, sg.Id
    FROM group_tree t
    JOIN mm_category_groups sg ON sg.group_key = t.descendant_group_id
  ),
  active_descendants AS (
    SELECT m_group.Id AS group_entry_id
    FROM monthly_budget_entries m_group
    WHERE m_group.ItemType = 0
      AND (m_group.Month1+m_group.Month2+m_group.Month3+m_group.Month4+m_group.Month5+m_group.Month6+m_group.Month7+m_group.Month8+m_group.Month9+m_group.Month10+m_group.Month11+m_group.Month12) != 0
      AND EXISTS (
        SELECT 1 FROM monthly_budget_entries m_other
        WHERE m_other.BudgetId = m_group.BudgetId
          AND m_other.Id != m_group.Id
          AND (m_other.Month1+m_other.Month2+m_other.Month3+m_other.Month4+m_other.Month5+m_other.Month6+m_other.Month7+m_other.Month8+m_other.Month9+m_other.Month10+m_other.Month11+m_other.Month12) != 0
          AND (
            (m_other.ItemType = 0 AND m_other.CategoryId IN (
              SELECT descendant_group_id FROM group_tree
              WHERE root_id = m_group.CategoryId AND descendant_group_id != m_group.CategoryId
            ))
            OR
            (m_other.ItemType = 1 AND m_other.CategoryId IN (
              SELECT c.Id FROM mm_categories c
              WHERE c.group_key IN (SELECT descendant_group_id FROM group_tree WHERE root_id = m_group.CategoryId)
            ))
          )
      )
  )
DELETE FROM monthly_budget_entries
WHERE Id IN (SELECT group_entry_id FROM active_descendants);
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // No-op: the migration only deletes redundant data, there's nothing to put back.
        }
    }
}
