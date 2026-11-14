using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenBudget.Migrations;

/// <inheritdoc />
public partial class _20261114141447_ProjectReceiptCategories : Migration
{
    /// <summary>
    /// "membership(cat, owner, project)": the projects (of the category's owner) a category belongs to — assigned itself
    /// or through one of the groups above it.
    /// </summary>
    private const string Membership = """
        WITH RECURSIVE chain(cat, owner, node) AS (
            SELECT Id, OwnerUserId, group_key FROM categories WHERE group_key IS NOT NULL
            UNION
            SELECT ch.cat, ch.owner, g.group_key FROM chain ch
            JOIN category_groups g ON g.Id = ch.node AND g.OwnerUserId = ch.owner
            WHERE g.group_key IS NOT NULL
        ),
        membership(cat, owner, project) AS (
            SELECT DISTINCT c.Id, c.OwnerUserId, p.Id FROM categories c
            JOIN projects p ON p.OwnerUserId = c.OwnerUserId
            JOIN project_category_assignments a ON a.ProjectId = p.Id
            WHERE a.CategoryId = c.Id OR a.CategoryId IN (SELECT node FROM chain WHERE chain.cat = c.Id AND chain.owner = c.OwnerUserId)
        )

        """;

    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_item_category_memory_OwnerUserId_NormalizedName",
            table: "item_category_memory");

        migrationBuilder.AddColumn<int>(
            name: "ProjectId",
            table: "item_category_memory",
            type: "INTEGER",
            nullable: false,
            defaultValue: 0);

        migrationBuilder.CreateTable(
            name: "project_receipt_categories",
            columns: table => new
            {
                Id = table.Column<long>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                OwnerUserId = table.Column<string>(type: "TEXT", maxLength: 450, nullable: false),
                ProjectId = table.Column<int>(type: "INTEGER", nullable: false),
                CategoryId = table.Column<long>(type: "INTEGER", nullable: false),
                Description = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                Keywords = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_project_receipt_categories", x => x.Id);
                table.ForeignKey(
                    name: "FK_project_receipt_categories_projects_ProjectId",
                    column: x => x.ProjectId,
                    principalTable: "projects",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "project_receipt_sources",
            columns: table => new
            {
                Id = table.Column<long>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                OwnerUserId = table.Column<string>(type: "TEXT", maxLength: 450, nullable: false),
                ProjectId = table.Column<int>(type: "INTEGER", nullable: false),
                Source = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                CategoryIds = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_project_receipt_sources", x => x.Id);
                table.ForeignKey(
                    name: "FK_project_receipt_sources_projects_ProjectId",
                    column: x => x.ProjectId,
                    principalTable: "projects",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        // Every per-source category becomes one of the project's categories (the projects whose category groups
        // contain it), keeping its description and keywords; per source the project's list is narrowed to what that
        // source had (Amazon had none), so the receipts split exactly as before. Memory entries go to every project
        // that contains their category; entries of categories in no project are dropped.
        migrationBuilder.Sql(Membership + """
            INSERT INTO project_receipt_categories (OwnerUserId, ProjectId, CategoryId, Description, Keywords)
            SELECT r.OwnerUserId, m.project, r.CategoryId, MAX(r.Description), MAX(r.Keywords)
            FROM receipt_source_categories r JOIN membership m ON m.cat = r.CategoryId AND m.owner = r.OwnerUserId
            GROUP BY r.OwnerUserId, m.project, r.CategoryId;
            """);
        migrationBuilder.Sql(Membership + """
            INSERT INTO project_receipt_sources (OwnerUserId, ProjectId, Source, CategoryIds)
            SELECT pr.OwnerUserId, pr.ProjectId, s.Source,
                   COALESCE((SELECT group_concat(x.CategoryId, ',') FROM (
                       SELECT DISTINCT r.CategoryId FROM receipt_source_categories r
                       JOIN membership m ON m.cat = r.CategoryId AND m.owner = r.OwnerUserId
                       WHERE r.OwnerUserId = pr.OwnerUserId AND m.project = pr.ProjectId AND r.Source = s.Source
                       ORDER BY r.CategoryId) x), '')
            FROM (SELECT DISTINCT OwnerUserId, ProjectId FROM project_receipt_categories) pr,
                 (SELECT 'mail' AS Source UNION ALL SELECT 'lidl' UNION ALL SELECT 'amazon') s;
            """);
        migrationBuilder.Sql(Membership + """
            INSERT INTO item_category_memory (OwnerUserId, NormalizedName, ProjectId, CategoryId, UpdatedAt)
            SELECT i.OwnerUserId, i.NormalizedName, m.project, i.CategoryId, i.UpdatedAt
            FROM item_category_memory i JOIN membership m ON m.cat = i.CategoryId AND m.owner = i.OwnerUserId
            WHERE i.ProjectId = 0;
            """);
        migrationBuilder.Sql("DELETE FROM item_category_memory WHERE ProjectId = 0;");
        migrationBuilder.Sql("DROP TABLE receipt_source_categories;");

        migrationBuilder.CreateIndex(
            name: "IX_item_category_memory_OwnerUserId_ProjectId_NormalizedName",
            table: "item_category_memory",
            columns: new[] { "OwnerUserId", "ProjectId", "NormalizedName" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_project_receipt_categories_ProjectId_CategoryId",
            table: "project_receipt_categories",
            columns: new[] { "ProjectId", "CategoryId" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_project_receipt_sources_ProjectId_Source",
            table: "project_receipt_sources",
            columns: new[] { "ProjectId", "Source" },
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "project_receipt_categories");

        migrationBuilder.DropTable(
            name: "project_receipt_sources");

        migrationBuilder.DropIndex(
            name: "IX_item_category_memory_OwnerUserId_ProjectId_NormalizedName",
            table: "item_category_memory");

        migrationBuilder.DropColumn(
            name: "ProjectId",
            table: "item_category_memory");

        migrationBuilder.CreateTable(
            name: "receipt_source_categories",
            columns: table => new
            {
                Id = table.Column<long>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                CategoryId = table.Column<long>(type: "INTEGER", nullable: false),
                Description = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                Keywords = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                OwnerUserId = table.Column<string>(type: "TEXT", maxLength: 450, nullable: false),
                Source = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_receipt_source_categories", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_item_category_memory_OwnerUserId_NormalizedName",
            table: "item_category_memory",
            columns: new[] { "OwnerUserId", "NormalizedName" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_receipt_source_categories_OwnerUserId_Source_CategoryId",
            table: "receipt_source_categories",
            columns: new[] { "OwnerUserId", "Source", "CategoryId" },
            unique: true);
    }
}
