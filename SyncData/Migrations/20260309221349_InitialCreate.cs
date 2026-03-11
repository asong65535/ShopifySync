using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SyncData.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ProductSyncMap",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    PcaItemNum = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    PcaUpc = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    ShopifyProductId = table.Column<long>(type: "bigint", nullable: false),
                    ShopifyVariantId = table.Column<long>(type: "bigint", nullable: false),
                    ShopifyInventoryItemId = table.Column<long>(type: "bigint", nullable: false),
                    ShopifyLocationId = table.Column<long>(type: "bigint", nullable: false),
                    LastKnownQty = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    LastSyncedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProductSyncMap", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SyncState",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    LastPolledAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncState", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SyncUnmatched",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    PcaItemNum = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    PcaItemName = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    PcaUpc = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Reason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    LoggedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncUnmatched", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ProductSyncMap");

            migrationBuilder.DropTable(
                name: "SyncState");

            migrationBuilder.DropTable(
                name: "SyncUnmatched");
        }
    }
}
