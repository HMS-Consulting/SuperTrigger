using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SuperTrigger.Web.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AdPrincipals",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Identifier = table.Column<string>(type: "TEXT", nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", nullable: false),
                    Type = table.Column<int>(type: "INTEGER", nullable: false),
                    Role = table.Column<int>(type: "INTEGER", nullable: false),
                    AddedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AdPrincipals", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "FileTriggers",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Active = table.Column<bool>(type: "INTEGER", nullable: false),
                    TriggerName = table.Column<string>(type: "TEXT", nullable: false),
                    FolderPath = table.Column<string>(type: "TEXT", nullable: false),
                    FileNameContains = table.Column<string>(type: "TEXT", nullable: false),
                    FileTypes = table.Column<string>(type: "TEXT", nullable: false),
                    Priority = table.Column<string>(type: "TEXT", nullable: false),
                    QueueName = table.Column<string>(type: "TEXT", nullable: false),
                    BusinessDepartmentName = table.Column<string>(type: "TEXT", nullable: false),
                    BusinessProcessName = table.Column<string>(type: "TEXT", nullable: false),
                    DivisionName = table.Column<string>(type: "TEXT", nullable: false),
                    CompanyName = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FileTriggers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LocalUsers",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Username = table.Column<string>(type: "TEXT", nullable: false),
                    PasswordHash = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LocalUsers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MailTriggers",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Active = table.Column<bool>(type: "INTEGER", nullable: false),
                    TriggerName = table.Column<string>(type: "TEXT", nullable: false),
                    UserName = table.Column<string>(type: "TEXT", nullable: false),
                    Password = table.Column<string>(type: "TEXT", nullable: false),
                    AzureTenantId = table.Column<string>(type: "TEXT", nullable: false),
                    CustomMailServer = table.Column<string>(type: "TEXT", nullable: false),
                    CustomMailServerVersion = table.Column<string>(type: "TEXT", nullable: false),
                    MailFolder = table.Column<string>(type: "TEXT", nullable: false),
                    SharedMailBox = table.Column<string>(type: "TEXT", nullable: false),
                    UserDomain = table.Column<string>(type: "TEXT", nullable: false),
                    SubjectFilterContains = table.Column<string>(type: "TEXT", nullable: false),
                    From = table.Column<string>(type: "TEXT", nullable: false),
                    To = table.Column<string>(type: "TEXT", nullable: false),
                    BodyFilterContains = table.Column<string>(type: "TEXT", nullable: false),
                    AttachmentsType = table.Column<string>(type: "TEXT", nullable: false),
                    Priority = table.Column<string>(type: "TEXT", nullable: false),
                    QueueName = table.Column<string>(type: "TEXT", nullable: false),
                    BusinessDepartmentName = table.Column<string>(type: "TEXT", nullable: false),
                    BusinessProcessName = table.Column<string>(type: "TEXT", nullable: false),
                    DivisionName = table.Column<string>(type: "TEXT", nullable: false),
                    CompanyName = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MailTriggers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "OrchSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    OrchestratorURL = table.Column<string>(type: "TEXT", nullable: false),
                    TenantName = table.Column<string>(type: "TEXT", nullable: false),
                    Username = table.Column<string>(type: "TEXT", nullable: false),
                    Password = table.Column<string>(type: "TEXT", nullable: false),
                    SSO = table.Column<bool>(type: "INTEGER", nullable: false),
                    AuthNewMethod = table.Column<bool>(type: "INTEGER", nullable: false),
                    ExternalAppScopes = table.Column<string>(type: "TEXT", nullable: false),
                    OrchestratorMainFolderName = table.Column<string>(type: "TEXT", nullable: false),
                    OrchestratorGeneralFolderName = table.Column<string>(type: "TEXT", nullable: false),
                    CyberArkSDKFilePath = table.Column<string>(type: "TEXT", nullable: false),
                    UseDefaultProxy = table.Column<bool>(type: "INTEGER", nullable: false),
                    MailTriggerIntervalInMinutes = table.Column<double>(type: "REAL", nullable: false),
                    SameFileIntervalInSeconds = table.Column<int>(type: "INTEGER", nullable: false),
                    MailServerURL = table.Column<string>(type: "TEXT", nullable: false),
                    MailServerVersion = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OrchSettings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "QueueItemLogs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    TriggerName = table.Column<string>(type: "TEXT", nullable: false),
                    TriggerType = table.Column<int>(type: "INTEGER", nullable: false),
                    QueueName = table.Column<string>(type: "TEXT", nullable: false),
                    Reference = table.Column<string>(type: "TEXT", nullable: false),
                    Payload = table.Column<string>(type: "TEXT", nullable: false),
                    Success = table.Column<bool>(type: "INTEGER", nullable: false),
                    ErrorMessage = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QueueItemLogs", x => x.Id);
                });

            migrationBuilder.InsertData(
                table: "OrchSettings",
                columns: new[] { "Id", "AuthNewMethod", "CyberArkSDKFilePath", "ExternalAppScopes", "MailServerURL", "MailServerVersion", "MailTriggerIntervalInMinutes", "OrchestratorGeneralFolderName", "OrchestratorMainFolderName", "OrchestratorURL", "Password", "SSO", "SameFileIntervalInSeconds", "TenantName", "UseDefaultProxy", "Username" },
                values: new object[] { 1, false, "", "OR.Assets.Read,OR.Queues", "", "Exchange2013_SP1", 0.5, ".General", "Root", "", "", false, 20, "", false, "" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AdPrincipals");

            migrationBuilder.DropTable(
                name: "FileTriggers");

            migrationBuilder.DropTable(
                name: "LocalUsers");

            migrationBuilder.DropTable(
                name: "MailTriggers");

            migrationBuilder.DropTable(
                name: "OrchSettings");

            migrationBuilder.DropTable(
                name: "QueueItemLogs");
        }
    }
}
