using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace SaaSApp.Users.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialPostgres : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "users");

            migrationBuilder.CreateTable(
                name: "Groups",
                schema: "users",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Description = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Groups", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Menus",
                schema: "users",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Label = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    RoutePath = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    IsSystem = table.Column<bool>(type: "boolean", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Menus", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PermissionCategories",
                schema: "users",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PermissionCategories", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Roles",
                schema: "users",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Description = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Roles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Users",
                schema: "users",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Email = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Role = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    FirstName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    LastName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    ProfileId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    PhoneNo = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    SecondaryEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    Language = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    CountryCode = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: true),
                    Department = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    JobTitle = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    ManagerId = table.Column<Guid>(type: "uuid", nullable: true),
                    UserType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    EmployeeId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    BusinessUnit = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    Location = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    GroupName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    AuthStrategy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    LoginType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    LoginName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    PasswordHash = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    PinHash = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    DeviceId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    TwoFactorAuthentication = table.Column<bool>(type: "boolean", nullable: false),
                    TotpSecretEncrypted = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    PasswordAge = table.Column<int>(type: "integer", nullable: true),
                    PasswordExpiryDays = table.Column<int>(type: "integer", nullable: false, defaultValue: 90),
                    AccountExpiryDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ForcePasswordResetOnLogin = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    MfaMethods = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    GoogleSubjectId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    MicrosoftOid = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    AvatarPath = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    IdCardPath = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    SignaturePath = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    UiPreference = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    Configuration = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    ModifiedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    ModifiedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Users", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "UserGroups",
                schema: "users",
                columns: table => new
                {
                    GroupId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserGroups", x => new { x.GroupId, x.UserId });
                    table.ForeignKey(
                        name: "FK_UserGroups_Groups_GroupId",
                        column: x => x.GroupId,
                        principalSchema: "users",
                        principalTable: "Groups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RoleMenus",
                schema: "users",
                columns: table => new
                {
                    RoleId = table.Column<Guid>(type: "uuid", nullable: false),
                    MenuId = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    IsDefaultLanding = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RoleMenus", x => new { x.RoleId, x.MenuId });
                    table.ForeignKey(
                        name: "FK_RoleMenus_Menus_MenuId",
                        column: x => x.MenuId,
                        principalSchema: "users",
                        principalTable: "Menus",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_RoleMenus_Roles_RoleId",
                        column: x => x.RoleId,
                        principalSchema: "users",
                        principalTable: "Roles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RolePermissions",
                schema: "users",
                columns: table => new
                {
                    RoleId = table.Column<Guid>(type: "uuid", nullable: false),
                    PermissionKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RolePermissions", x => new { x.RoleId, x.PermissionKey });
                    table.ForeignKey(
                        name: "FK_RolePermissions_Roles_RoleId",
                        column: x => x.RoleId,
                        principalSchema: "users",
                        principalTable: "Roles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "UserRoles",
                schema: "users",
                columns: table => new
                {
                    RoleId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserRoles", x => new { x.RoleId, x.UserId });
                    table.ForeignKey(
                        name: "FK_UserRoles_Roles_RoleId",
                        column: x => x.RoleId,
                        principalSchema: "users",
                        principalTable: "Roles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.InsertData(
                schema: "users",
                table: "Menus",
                columns: new[] { "Id", "CreatedAtUtc", "IsDeleted", "IsSystem", "Key", "Label", "RoutePath", "SortOrder" },
                values: new object[,]
                {
                    { new Guid("b2000001-0000-4000-8000-000000000001"), new DateTime(2026, 8, 10, 11, 1, 15, 839, DateTimeKind.Utc).AddTicks(6847), false, true, "dashboard", "Dashboard", "/dashboard", 1 },
                    { new Guid("b2000001-0000-4000-8000-000000000002"), new DateTime(2026, 8, 10, 11, 1, 15, 839, DateTimeKind.Utc).AddTicks(6898), false, true, "inbox", "Inbox", "/inbox", 2 },
                    { new Guid("b2000001-0000-4000-8000-000000000003"), new DateTime(2026, 8, 10, 11, 1, 15, 839, DateTimeKind.Utc).AddTicks(6906), false, true, "ocr-review", "OCR.Review", "/ocr-review", 3 },
                    { new Guid("b2000001-0000-4000-8000-000000000004"), new DateTime(2026, 8, 10, 11, 1, 15, 839, DateTimeKind.Utc).AddTicks(6913), false, true, "processed-invoices", "Processed Invoices", "/processed-invoices", 4 },
                    { new Guid("b2000001-0000-4000-8000-000000000005"), new DateTime(2026, 8, 10, 11, 1, 15, 839, DateTimeKind.Utc).AddTicks(6920), false, true, "approval-queue", "Approval Queue", "/approval-queue", 5 },
                    { new Guid("b2000001-0000-4000-8000-000000000006"), new DateTime(2026, 8, 10, 11, 1, 15, 839, DateTimeKind.Utc).AddTicks(6931), false, true, "vendors", "Vendors", "/vendors", 6 }
                });

            migrationBuilder.InsertData(
                schema: "users",
                table: "PermissionCategories",
                columns: new[] { "Id", "IsActive", "Key", "Name", "SortOrder" },
                values: new object[,]
                {
                    { new Guid("a1000001-0000-4000-8000-000000000001"), true, "workflow", "Workflow", 2 },
                    { new Guid("a1000001-0000-4000-8000-000000000002"), true, "folder", "Folder", 3 },
                    { new Guid("a1000001-0000-4000-8000-000000000003"), true, "task", "Task", 4 },
                    { new Guid("a1000001-0000-4000-8000-000000000004"), true, "workspace", "Workspace", 5 },
                    { new Guid("a1000001-0000-4000-8000-000000000005"), true, "settings", "Settings", 6 },
                    { new Guid("a1000001-0000-4000-8000-000000000006"), true, "dashboard", "Dashboard", 1 }
                });

            migrationBuilder.CreateIndex(
                name: "IX_Groups_TenantId_Name",
                schema: "users",
                table: "Groups",
                columns: new[] { "TenantId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Menus_Key",
                schema: "users",
                table: "Menus",
                column: "Key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PermissionCategories_Key",
                schema: "users",
                table: "PermissionCategories",
                column: "Key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RoleMenus_MenuId",
                schema: "users",
                table: "RoleMenus",
                column: "MenuId");

            migrationBuilder.CreateIndex(
                name: "IX_Roles_TenantId_Name",
                schema: "users",
                table: "Roles",
                columns: new[] { "TenantId", "Name" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PermissionCategories",
                schema: "users");

            migrationBuilder.DropTable(
                name: "RoleMenus",
                schema: "users");

            migrationBuilder.DropTable(
                name: "RolePermissions",
                schema: "users");

            migrationBuilder.DropTable(
                name: "UserGroups",
                schema: "users");

            migrationBuilder.DropTable(
                name: "UserRoles",
                schema: "users");

            migrationBuilder.DropTable(
                name: "Users",
                schema: "users");

            migrationBuilder.DropTable(
                name: "Menus",
                schema: "users");

            migrationBuilder.DropTable(
                name: "Groups",
                schema: "users");

            migrationBuilder.DropTable(
                name: "Roles",
                schema: "users");
        }
    }
}
