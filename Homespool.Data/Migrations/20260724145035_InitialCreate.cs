using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Homespool.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AspNetRoles",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    NormalizedName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    ConcurrencyStamp = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetRoles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUsers",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    NormalizedUserName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    Email = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    NormalizedEmail = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    EmailConfirmed = table.Column<bool>(type: "INTEGER", nullable: false),
                    PasswordHash = table.Column<string>(type: "TEXT", nullable: true),
                    SecurityStamp = table.Column<string>(type: "TEXT", nullable: true),
                    ConcurrencyStamp = table.Column<string>(type: "TEXT", nullable: true),
                    PhoneNumber = table.Column<string>(type: "TEXT", nullable: true),
                    PhoneNumberConfirmed = table.Column<bool>(type: "INTEGER", nullable: false),
                    TwoFactorEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    LockoutEnd = table.Column<long>(type: "INTEGER", nullable: true),
                    LockoutEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    AccessFailedCount = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUsers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DataProtectionKeys",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    FriendlyName = table.Column<string>(type: "TEXT", nullable: true),
                    Xml = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DataProtectionKeys", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Teams",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedBy = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Teams", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AspNetRoleClaims",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    RoleId = table.Column<long>(type: "INTEGER", nullable: false),
                    ClaimType = table.Column<string>(type: "TEXT", nullable: true),
                    ClaimValue = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetRoleClaims", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AspNetRoleClaims_AspNetRoles_RoleId",
                        column: x => x.RoleId,
                        principalTable: "AspNetRoles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUserClaims",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserId = table.Column<long>(type: "INTEGER", nullable: false),
                    ClaimType = table.Column<string>(type: "TEXT", nullable: true),
                    ClaimValue = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUserClaims", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AspNetUserClaims_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUserLogins",
                columns: table => new
                {
                    LoginProvider = table.Column<string>(type: "TEXT", nullable: false),
                    ProviderKey = table.Column<string>(type: "TEXT", nullable: false),
                    ProviderDisplayName = table.Column<string>(type: "TEXT", nullable: true),
                    UserId = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUserLogins", x => new { x.LoginProvider, x.ProviderKey });
                    table.ForeignKey(
                        name: "FK_AspNetUserLogins_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUserRoles",
                columns: table => new
                {
                    UserId = table.Column<long>(type: "INTEGER", nullable: false),
                    RoleId = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUserRoles", x => new { x.UserId, x.RoleId });
                    table.ForeignKey(
                        name: "FK_AspNetUserRoles_AspNetRoles_RoleId",
                        column: x => x.RoleId,
                        principalTable: "AspNetRoles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AspNetUserRoles_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUserTokens",
                columns: table => new
                {
                    UserId = table.Column<long>(type: "INTEGER", nullable: false),
                    LoginProvider = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Value = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUserTokens", x => new { x.UserId, x.LoginProvider, x.Name });
                    table.ForeignKey(
                        name: "FK_AspNetUserTokens_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Invitations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    HashedToken = table.Column<string>(type: "TEXT", nullable: false),
                    Email = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    ExpiresAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UsedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    InvitedBy = table.Column<long>(type: "INTEGER", nullable: false),
                    TeamId = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Invitations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Invitations_Teams_TeamId",
                        column: x => x.TeamId,
                        principalTable: "Teams",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Printers",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Uuid = table.Column<Guid>(type: "TEXT", nullable: false),
                    Type = table.Column<int>(type: "INTEGER", nullable: false),
                    TeamId = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: true),
                    Model = table.Column<string>(type: "TEXT", nullable: true),
                    Location = table.Column<string>(type: "TEXT", nullable: true),
                    Firmware = table.Column<string>(type: "TEXT", nullable: true),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    LoadedMaterial = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Printers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Printers_Teams_TeamId",
                        column: x => x.TeamId,
                        principalTable: "Teams",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TeamMembers",
                columns: table => new
                {
                    TeamId = table.Column<int>(type: "INTEGER", nullable: false),
                    UserId = table.Column<long>(type: "INTEGER", nullable: false),
                    CanRead = table.Column<bool>(type: "INTEGER", nullable: false),
                    CanUse = table.Column<bool>(type: "INTEGER", nullable: false),
                    CanManage = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsDefault = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TeamMembers", x => new { x.TeamId, x.UserId });
                    table.ForeignKey(
                        name: "FK_TeamMembers_Teams_TeamId",
                        column: x => x.TeamId,
                        principalTable: "Teams",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PrinterEvents",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PrinterId = table.Column<int>(type: "INTEGER", nullable: false),
                    Timestamp = table.Column<long>(type: "INTEGER", nullable: false),
                    EventType = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    JobId = table.Column<int>(type: "INTEGER", nullable: true),
                    CommandId = table.Column<long>(type: "INTEGER", nullable: true),
                    Reason = table.Column<string>(type: "TEXT", nullable: true),
                    Payload = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PrinterEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PrinterEvents_Printers_PrinterId",
                        column: x => x.PrinterId,
                        principalTable: "Printers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PrinterLiveStates",
                columns: table => new
                {
                    PrinterId = table.Column<int>(type: "INTEGER", nullable: false),
                    LastSeenAt = table.Column<long>(type: "INTEGER", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    JobId = table.Column<int>(type: "INTEGER", nullable: true),
                    Progress = table.Column<int>(type: "INTEGER", nullable: true),
                    TimePrinting = table.Column<int>(type: "INTEGER", nullable: true),
                    TimeRemaining = table.Column<int>(type: "INTEGER", nullable: true),
                    NozzleTemperature = table.Column<float>(type: "REAL", nullable: true),
                    BedTemperature = table.Column<float>(type: "REAL", nullable: true),
                    TargetNozzleTemperature = table.Column<float>(type: "REAL", nullable: true),
                    TargetBedTemperature = table.Column<float>(type: "REAL", nullable: true),
                    Speed = table.Column<int>(type: "INTEGER", nullable: true),
                    Flow = table.Column<int>(type: "INTEGER", nullable: true),
                    Material = table.Column<string>(type: "TEXT", nullable: true),
                    XAxis = table.Column<float>(type: "REAL", nullable: true),
                    YAxis = table.Column<float>(type: "REAL", nullable: true),
                    ZAxis = table.Column<float>(type: "REAL", nullable: true),
                    ExtruderFan = table.Column<int>(type: "INTEGER", nullable: true),
                    PrintFan = table.Column<int>(type: "INTEGER", nullable: true),
                    FilamentUsed = table.Column<float>(type: "REAL", nullable: true),
                    TimeToFilamentChange = table.Column<int>(type: "INTEGER", nullable: true),
                    ChamberTemperature = table.Column<float>(type: "REAL", nullable: true),
                    ChamberTargetTemperature = table.Column<int>(type: "INTEGER", nullable: true),
                    ChamberFan1Rpm = table.Column<int>(type: "INTEGER", nullable: true),
                    ChamberFan2Rpm = table.Column<int>(type: "INTEGER", nullable: true),
                    ChamberFanPwmTarget = table.Column<int>(type: "INTEGER", nullable: true),
                    ChamberLedIntensity = table.Column<int>(type: "INTEGER", nullable: true),
                    EnclosureTemperature = table.Column<int>(type: "INTEGER", nullable: true),
                    EnclosureFanRpm = table.Column<int>(type: "INTEGER", nullable: true),
                    EnclosureTimeInUse = table.Column<int>(type: "INTEGER", nullable: true),
                    HeatbreakTemperature = table.Column<float>(type: "REAL", nullable: true),
                    PsuTemperature = table.Column<float>(type: "REAL", nullable: true),
                    AmbientTemperature = table.Column<float>(type: "REAL", nullable: true),
                    ExtruderFilamentSensorStatus = table.Column<string>(type: "TEXT", nullable: true),
                    RemoteFilamentSensorStatus = table.Column<string>(type: "TEXT", nullable: true),
                    ActiveSlot = table.Column<int>(type: "INTEGER", nullable: true),
                    MmuState = table.Column<int>(type: "INTEGER", nullable: true),
                    MmuCommand = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PrinterLiveStates", x => x.PrinterId);
                    table.ForeignKey(
                        name: "FK_PrinterLiveStates_Printers_PrinterId",
                        column: x => x.PrinterId,
                        principalTable: "Printers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PrusaConnectAuthentication",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PrinterId = table.Column<int>(type: "INTEGER", nullable: false),
                    FingerPrintKey = table.Column<string>(type: "TEXT", nullable: false),
                    FullFingerPrint = table.Column<string>(type: "TEXT", nullable: true),
                    HashedToken = table.Column<string>(type: "TEXT", nullable: false),
                    EnrolledAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PrusaConnectAuthentication", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PrusaConnectAuthentication_Printers_PrinterId",
                        column: x => x.PrinterId,
                        principalTable: "Printers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PrusaConnectProvisionings",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PrinterId = table.Column<int>(type: "INTEGER", nullable: false),
                    HashedToken = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PrusaConnectProvisionings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PrusaConnectProvisionings_Printers_PrinterId",
                        column: x => x.PrinterId,
                        principalTable: "Printers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PrusaConnectRegistrations",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PrinterId = table.Column<int>(type: "INTEGER", nullable: true),
                    SerialNumber = table.Column<string>(type: "TEXT", nullable: false),
                    FingerPrint = table.Column<string>(type: "TEXT", nullable: false),
                    TemporaryCode = table.Column<string>(type: "TEXT", nullable: false),
                    TemporaryCodeExpiry = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PrusaConnectRegistrations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PrusaConnectRegistrations_Printers_PrinterId",
                        column: x => x.PrinterId,
                        principalTable: "Printers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TelemetrySamples",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PrinterId = table.Column<int>(type: "INTEGER", nullable: false),
                    Timestamp = table.Column<long>(type: "INTEGER", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    JobId = table.Column<int>(type: "INTEGER", nullable: true),
                    Progress = table.Column<int>(type: "INTEGER", nullable: true),
                    TimePrinting = table.Column<int>(type: "INTEGER", nullable: true),
                    TimeRemaining = table.Column<int>(type: "INTEGER", nullable: true),
                    NozzleTemperature = table.Column<float>(type: "REAL", nullable: true),
                    BedTemperature = table.Column<float>(type: "REAL", nullable: true),
                    TargetNozzleTemperature = table.Column<float>(type: "REAL", nullable: true),
                    TargetBedTemperature = table.Column<float>(type: "REAL", nullable: true),
                    Speed = table.Column<int>(type: "INTEGER", nullable: true),
                    Flow = table.Column<int>(type: "INTEGER", nullable: true),
                    Material = table.Column<string>(type: "TEXT", nullable: true),
                    XAxis = table.Column<float>(type: "REAL", nullable: true),
                    YAxis = table.Column<float>(type: "REAL", nullable: true),
                    ZAxis = table.Column<float>(type: "REAL", nullable: true),
                    ExtruderFan = table.Column<int>(type: "INTEGER", nullable: true),
                    PrintFan = table.Column<int>(type: "INTEGER", nullable: true),
                    FilamentUsed = table.Column<float>(type: "REAL", nullable: true),
                    TimeToFilamentChange = table.Column<int>(type: "INTEGER", nullable: true),
                    ChamberTemperature = table.Column<float>(type: "REAL", nullable: true),
                    ChamberTargetTemperature = table.Column<int>(type: "INTEGER", nullable: true),
                    ChamberFan1Rpm = table.Column<int>(type: "INTEGER", nullable: true),
                    ChamberFan2Rpm = table.Column<int>(type: "INTEGER", nullable: true),
                    ChamberFanPwmTarget = table.Column<int>(type: "INTEGER", nullable: true),
                    ChamberLedIntensity = table.Column<int>(type: "INTEGER", nullable: true),
                    EnclosureTemperature = table.Column<int>(type: "INTEGER", nullable: true),
                    EnclosureFanRpm = table.Column<int>(type: "INTEGER", nullable: true),
                    EnclosureTimeInUse = table.Column<int>(type: "INTEGER", nullable: true),
                    HeatbreakTemperature = table.Column<float>(type: "REAL", nullable: true),
                    PsuTemperature = table.Column<float>(type: "REAL", nullable: true),
                    AmbientTemperature = table.Column<float>(type: "REAL", nullable: true),
                    ActiveSlot = table.Column<int>(type: "INTEGER", nullable: true),
                    MmuState = table.Column<int>(type: "INTEGER", nullable: true),
                    MmuCommand = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TelemetrySamples", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TelemetrySamples_Printers_PrinterId",
                        column: x => x.PrinterId,
                        principalTable: "Printers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PrinterLiveSlotStates",
                columns: table => new
                {
                    PrinterId = table.Column<int>(type: "INTEGER", nullable: false),
                    SlotNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    Material = table.Column<string>(type: "TEXT", nullable: true),
                    Temperature = table.Column<float>(type: "REAL", nullable: true),
                    HotendFanRpm = table.Column<float>(type: "REAL", nullable: true),
                    PrintFanRpm = table.Column<float>(type: "REAL", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PrinterLiveSlotStates", x => new { x.PrinterId, x.SlotNumber });
                    table.ForeignKey(
                        name: "FK_PrinterLiveSlotStates_PrinterLiveStates_PrinterId",
                        column: x => x.PrinterId,
                        principalTable: "PrinterLiveStates",
                        principalColumn: "PrinterId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TelemetrySlotSamples",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TelemetrySampleId = table.Column<long>(type: "INTEGER", nullable: false),
                    SlotNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    Material = table.Column<string>(type: "TEXT", nullable: true),
                    Temperature = table.Column<float>(type: "REAL", nullable: true),
                    HotendFanRpm = table.Column<float>(type: "REAL", nullable: true),
                    PrintFanRpm = table.Column<float>(type: "REAL", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TelemetrySlotSamples", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TelemetrySlotSamples_TelemetrySamples_TelemetrySampleId",
                        column: x => x.TelemetrySampleId,
                        principalTable: "TelemetrySamples",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AspNetRoleClaims_RoleId",
                table: "AspNetRoleClaims",
                column: "RoleId");

            migrationBuilder.CreateIndex(
                name: "RoleNameIndex",
                table: "AspNetRoles",
                column: "NormalizedName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUserClaims_UserId",
                table: "AspNetUserClaims",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUserLogins_UserId",
                table: "AspNetUserLogins",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUserRoles_RoleId",
                table: "AspNetUserRoles",
                column: "RoleId");

            migrationBuilder.CreateIndex(
                name: "EmailIndex",
                table: "AspNetUsers",
                column: "NormalizedEmail");

            migrationBuilder.CreateIndex(
                name: "UserNameIndex",
                table: "AspNetUsers",
                column: "NormalizedUserName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Invitations_HashedToken",
                table: "Invitations",
                column: "HashedToken",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Invitations_TeamId",
                table: "Invitations",
                column: "TeamId");

            migrationBuilder.CreateIndex(
                name: "IX_PrinterEvents_PrinterId_JobId",
                table: "PrinterEvents",
                columns: new[] { "PrinterId", "JobId" });

            migrationBuilder.CreateIndex(
                name: "IX_PrinterEvents_PrinterId_Timestamp",
                table: "PrinterEvents",
                columns: new[] { "PrinterId", "Timestamp" });

            migrationBuilder.CreateIndex(
                name: "IX_Printers_TeamId",
                table: "Printers",
                column: "TeamId");

            migrationBuilder.CreateIndex(
                name: "IX_Printers_Uuid",
                table: "Printers",
                column: "Uuid",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PrusaConnectAuthentication_FingerPrintKey",
                table: "PrusaConnectAuthentication",
                column: "FingerPrintKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PrusaConnectAuthentication_PrinterId",
                table: "PrusaConnectAuthentication",
                column: "PrinterId");

            migrationBuilder.CreateIndex(
                name: "IX_PrusaConnectProvisionings_PrinterId",
                table: "PrusaConnectProvisionings",
                column: "PrinterId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PrusaConnectRegistrations_FingerPrint",
                table: "PrusaConnectRegistrations",
                column: "FingerPrint",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PrusaConnectRegistrations_PrinterId",
                table: "PrusaConnectRegistrations",
                column: "PrinterId");

            migrationBuilder.CreateIndex(
                name: "IX_PrusaConnectRegistrations_TemporaryCode",
                table: "PrusaConnectRegistrations",
                column: "TemporaryCode");

            migrationBuilder.CreateIndex(
                name: "IX_TeamMembers_UserId",
                table: "TeamMembers",
                column: "UserId",
                unique: true,
                filter: "\"IsDefault\"");

            migrationBuilder.CreateIndex(
                name: "IX_TelemetrySamples_PrinterId_Timestamp",
                table: "TelemetrySamples",
                columns: new[] { "PrinterId", "Timestamp" });

            migrationBuilder.CreateIndex(
                name: "IX_TelemetrySlotSamples_TelemetrySampleId_SlotNumber",
                table: "TelemetrySlotSamples",
                columns: new[] { "TelemetrySampleId", "SlotNumber" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AspNetRoleClaims");

            migrationBuilder.DropTable(
                name: "AspNetUserClaims");

            migrationBuilder.DropTable(
                name: "AspNetUserLogins");

            migrationBuilder.DropTable(
                name: "AspNetUserRoles");

            migrationBuilder.DropTable(
                name: "AspNetUserTokens");

            migrationBuilder.DropTable(
                name: "DataProtectionKeys");

            migrationBuilder.DropTable(
                name: "Invitations");

            migrationBuilder.DropTable(
                name: "PrinterEvents");

            migrationBuilder.DropTable(
                name: "PrinterLiveSlotStates");

            migrationBuilder.DropTable(
                name: "PrusaConnectAuthentication");

            migrationBuilder.DropTable(
                name: "PrusaConnectProvisionings");

            migrationBuilder.DropTable(
                name: "PrusaConnectRegistrations");

            migrationBuilder.DropTable(
                name: "TeamMembers");

            migrationBuilder.DropTable(
                name: "TelemetrySlotSamples");

            migrationBuilder.DropTable(
                name: "AspNetRoles");

            migrationBuilder.DropTable(
                name: "AspNetUsers");

            migrationBuilder.DropTable(
                name: "PrinterLiveStates");

            migrationBuilder.DropTable(
                name: "TelemetrySamples");

            migrationBuilder.DropTable(
                name: "Printers");

            migrationBuilder.DropTable(
                name: "Teams");
        }
    }
}
