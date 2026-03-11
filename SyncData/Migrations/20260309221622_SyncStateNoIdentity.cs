using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SyncData.Migrations
{
    /// <inheritdoc />
    public partial class SyncStateNoIdentity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // SQL Server cannot alter an IDENTITY column in-place; drop and recreate.
            migrationBuilder.DropTable(name: "SyncState");
            migrationBuilder.CreateTable(
                name: "SyncState",
                columns: table => new
                {
                    Id          = table.Column<int>(type: "int", nullable: false),
                    LastPolledAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncState", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "SyncState");
            migrationBuilder.CreateTable(
                name: "SyncState",
                columns: table => new
                {
                    Id          = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    LastPolledAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncState", x => x.Id);
                });
        }
    }
}
