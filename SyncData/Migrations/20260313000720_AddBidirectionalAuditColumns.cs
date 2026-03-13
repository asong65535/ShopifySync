using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SyncData.Migrations
{
    /// <inheritdoc />
    public partial class AddBidirectionalAuditColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "LastKnownPcaQty",
                table: "ProductSyncMap",
                type: "decimal(18,4)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "LastKnownShopifyQty",
                table: "ProductSyncMap",
                type: "decimal(18,4)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.Sql(
                "UPDATE ProductSyncMap SET LastKnownPcaQty = LastKnownQty, LastKnownShopifyQty = LastKnownQty");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LastKnownPcaQty",
                table: "ProductSyncMap");

            migrationBuilder.DropColumn(
                name: "LastKnownShopifyQty",
                table: "ProductSyncMap");
        }
    }
}
