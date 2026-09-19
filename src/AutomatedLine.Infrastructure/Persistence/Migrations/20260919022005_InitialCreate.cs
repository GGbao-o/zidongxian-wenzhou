using System;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AutomatedLine.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "CraneConfig",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    No = table.Column<int>(type: "int", nullable: false),
                    Name = table.Column<string>(type: "varchar(50)", maxLength: 50, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Protocol = table.Column<string>(type: "varchar(30)", maxLength: 30, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    IpAddress = table.Column<string>(type: "varchar(50)", maxLength: 50, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Port = table.Column<int>(type: "int", nullable: false),
                    StartX = table.Column<double>(type: "double", nullable: false),
                    EndX = table.Column<double>(type: "double", nullable: false),
                    Width = table.Column<double>(type: "double", nullable: false),
                    SafeX = table.Column<double>(type: "double", nullable: false),
                    SafeZ = table.Column<double>(type: "double", nullable: false),
                    SpeedX = table.Column<double>(type: "double", nullable: false),
                    SpeedY = table.Column<double>(type: "double", nullable: false),
                    SpeedZ = table.Column<double>(type: "double", nullable: false),
                    AccelX = table.Column<double>(type: "double", nullable: false),
                    AccelY = table.Column<double>(type: "double", nullable: false),
                    AccelZ = table.Column<double>(type: "double", nullable: false),
                    DecelX = table.Column<double>(type: "double", nullable: false),
                    DecelY = table.Column<double>(type: "double", nullable: false),
                    DecelZ = table.Column<double>(type: "double", nullable: false),
                    IsEnabled = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    Remark = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    UpdatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CraneConfig", x => x.Id);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "DeviceConfig",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    Name = table.Column<string>(type: "varchar(50)", maxLength: 50, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    DeviceType = table.Column<string>(type: "varchar(30)", maxLength: 30, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Protocol = table.Column<string>(type: "varchar(30)", maxLength: 30, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    IpAddress = table.Column<string>(type: "varchar(50)", maxLength: 50, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Port = table.Column<int>(type: "int", nullable: false),
                    PositionX = table.Column<double>(type: "double", nullable: false),
                    SafePosition = table.Column<double>(type: "double", nullable: false),
                    MinDiameter = table.Column<double>(type: "double", nullable: false),
                    MaxDiameter = table.Column<double>(type: "double", nullable: false),
                    MinLength = table.Column<double>(type: "double", nullable: false),
                    MaxLength = table.Column<double>(type: "double", nullable: false),
                    IsEnabled = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    Remark = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    UpdatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeviceConfig", x => x.Id);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_CraneConfig_No",
                table: "CraneConfig",
                column: "No",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CraneConfig");

            migrationBuilder.DropTable(
                name: "DeviceConfig");
        }
    }
}
