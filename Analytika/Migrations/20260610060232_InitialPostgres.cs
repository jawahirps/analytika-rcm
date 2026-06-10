using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Analytika.Migrations
{
    /// <inheritdoc />
    public partial class InitialPostgres : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AspNetRoles",
                columns: table => new
                {
                    Id = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    NormalizedName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    ConcurrencyStamp = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetRoles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUsers",
                columns: table => new
                {
                    Id = table.Column<string>(type: "text", nullable: false),
                    FullName = table.Column<string>(type: "text", nullable: true),
                    Department = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    UserType = table.Column<string>(type: "text", nullable: false),
                    UserName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    NormalizedUserName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    Email = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    NormalizedEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    EmailConfirmed = table.Column<bool>(type: "boolean", nullable: false),
                    PasswordHash = table.Column<string>(type: "text", nullable: true),
                    SecurityStamp = table.Column<string>(type: "text", nullable: true),
                    ConcurrencyStamp = table.Column<string>(type: "text", nullable: true),
                    PhoneNumber = table.Column<string>(type: "text", nullable: true),
                    PhoneNumberConfirmed = table.Column<bool>(type: "boolean", nullable: false),
                    TwoFactorEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    LockoutEnd = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LockoutEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    AccessFailedCount = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUsers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Clinicians",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "text", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Clinicians", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Departments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "text", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Departments", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DhpoCodingSets",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Category = table.Column<string>(type: "text", nullable: false),
                    Code = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    SubType = table.Column<string>(type: "text", nullable: true),
                    ExtraJson = table.Column<string>(type: "text", nullable: true),
                    ImportedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DhpoCodingSets", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Facilities",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "text", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Facilities", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Payers",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "text", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Payers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Receivers",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "text", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Receivers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ReportSchedules",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "text", nullable: false),
                    ReportType = table.Column<string>(type: "text", nullable: false),
                    CronExpression = table.Column<string>(type: "text", nullable: false),
                    Recipients = table.Column<string>(type: "text", nullable: false),
                    FileFormat = table.Column<string>(type: "text", nullable: false),
                    FacilityIdsJson = table.Column<string>(type: "text", nullable: true),
                    ParametersJson = table.Column<string>(type: "text", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    LastRunAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    LastRunStatus = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReportSchedules", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SystemSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Category = table.Column<string>(type: "text", nullable: false),
                    Key = table.Column<string>(type: "text", nullable: false),
                    Value = table.Column<string>(type: "text", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SystemSettings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AspNetRoleClaims",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    RoleId = table.Column<string>(type: "text", nullable: false),
                    ClaimType = table.Column<string>(type: "text", nullable: true),
                    ClaimValue = table.Column<string>(type: "text", nullable: true)
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
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<string>(type: "text", nullable: false),
                    ClaimType = table.Column<string>(type: "text", nullable: true),
                    ClaimValue = table.Column<string>(type: "text", nullable: true)
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
                    LoginProvider = table.Column<string>(type: "text", nullable: false),
                    ProviderKey = table.Column<string>(type: "text", nullable: false),
                    ProviderDisplayName = table.Column<string>(type: "text", nullable: true),
                    UserId = table.Column<string>(type: "text", nullable: false)
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
                    UserId = table.Column<string>(type: "text", nullable: false),
                    RoleId = table.Column<string>(type: "text", nullable: false)
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
                    UserId = table.Column<string>(type: "text", nullable: false),
                    LoginProvider = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Value = table.Column<string>(type: "text", nullable: true)
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
                name: "UserReportAccesses",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<string>(type: "text", nullable: false),
                    ResourceType = table.Column<string>(type: "text", nullable: false),
                    ResourceKey = table.Column<string>(type: "text", nullable: false),
                    CanView = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserReportAccesses", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UserReportAccesses_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PortalCredentials",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Portal = table.Column<string>(type: "text", nullable: false),
                    FacilityId = table.Column<int>(type: "integer", nullable: false),
                    CredentialName = table.Column<string>(type: "text", nullable: true),
                    Username = table.Column<string>(type: "text", nullable: false),
                    PasswordEncrypted = table.Column<string>(type: "text", nullable: false),
                    ApiBaseUrl = table.Column<string>(type: "text", nullable: true),
                    LicenseCode = table.Column<string>(type: "text", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PortalCredentials", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PortalCredentials_Facilities_FacilityId",
                        column: x => x.FacilityId,
                        principalTable: "Facilities",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PortalFetchLogs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Portal = table.Column<string>(type: "text", nullable: false),
                    FacilityId = table.Column<int>(type: "integer", nullable: false),
                    Operation = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    ResponseSummary = table.Column<string>(type: "text", nullable: true),
                    RecordsFetched = table.Column<int>(type: "integer", nullable: false),
                    FetchedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    FetchedBy = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PortalFetchLogs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PortalFetchLogs_Facilities_FacilityId",
                        column: x => x.FacilityId,
                        principalTable: "Facilities",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PortalTransactions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Portal = table.Column<string>(type: "text", nullable: false),
                    FacilityId = table.Column<int>(type: "integer", nullable: false),
                    TransactionId = table.Column<string>(type: "text", nullable: false),
                    Type = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    Direction = table.Column<string>(type: "text", nullable: true),
                    FileId = table.Column<string>(type: "text", nullable: true),
                    FileName = table.Column<string>(type: "text", nullable: true),
                    FileDownloaded = table.Column<bool>(type: "boolean", nullable: false),
                    FileContentXml = table.Column<string>(type: "text", nullable: true),
                    FileSizeBytes = table.Column<long>(type: "bigint", nullable: true),
                    FileDownloadedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    TransactionDate = table.Column<string>(type: "text", nullable: true),
                    Payer = table.Column<string>(type: "text", nullable: true),
                    Amount = table.Column<string>(type: "text", nullable: true),
                    RawXml = table.Column<string>(type: "text", nullable: true),
                    Operation = table.Column<string>(type: "text", nullable: false),
                    SyncPeriod = table.Column<string>(type: "text", nullable: true),
                    SyncedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PortalTransactions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PortalTransactions_Facilities_FacilityId",
                        column: x => x.FacilityId,
                        principalTable: "Facilities",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "UserFacilities",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<string>(type: "text", nullable: false),
                    FacilityId = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserFacilities", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UserFacilities_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_UserFacilities_Facilities_FacilityId",
                        column: x => x.FacilityId,
                        principalTable: "Facilities",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ReportRequests",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ReportId = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ReportType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    BranchId = table.Column<int>(type: "integer", nullable: true),
                    ReceiverId = table.Column<int>(type: "integer", nullable: true),
                    PayerId = table.Column<int>(type: "integer", nullable: true),
                    ClinicianId = table.Column<int>(type: "integer", nullable: true),
                    DepartmentId = table.Column<int>(type: "integer", nullable: true),
                    EncounterType = table.Column<string>(type: "text", nullable: true),
                    DateFrom = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    DateTo = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    SearchCriteria = table.Column<string>(type: "text", nullable: true),
                    Template = table.Column<string>(type: "text", nullable: true),
                    FileFormat = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Status = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    RequestedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    GeneratedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    FilePath = table.Column<string>(type: "text", nullable: true),
                    RequestedBy = table.Column<string>(type: "text", nullable: true),
                    EmailTo = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReportRequests", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ReportRequests_Clinicians_ClinicianId",
                        column: x => x.ClinicianId,
                        principalTable: "Clinicians",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_ReportRequests_Departments_DepartmentId",
                        column: x => x.DepartmentId,
                        principalTable: "Departments",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_ReportRequests_Facilities_BranchId",
                        column: x => x.BranchId,
                        principalTable: "Facilities",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_ReportRequests_Payers_PayerId",
                        column: x => x.PayerId,
                        principalTable: "Payers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_ReportRequests_Receivers_ReceiverId",
                        column: x => x.ReceiverId,
                        principalTable: "Receivers",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "RemittanceClaims",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    RemittanceTransactionId = table.Column<int>(type: "integer", nullable: false),
                    FacilityId = table.Column<int>(type: "integer", nullable: false),
                    ClaimId = table.Column<string>(type: "text", nullable: false),
                    PayerClaimId = table.Column<string>(type: "text", nullable: true),
                    PayerCode = table.Column<string>(type: "text", nullable: true),
                    ClinicianLicense = table.Column<string>(type: "text", nullable: true),
                    OriginalAmount = table.Column<decimal>(type: "numeric", nullable: false),
                    PaidAmount = table.Column<decimal>(type: "numeric", nullable: false),
                    DenialCodesJson = table.Column<string>(type: "text", nullable: true),
                    Comments = table.Column<string>(type: "text", nullable: true),
                    ActivityCount = table.Column<int>(type: "integer", nullable: false),
                    SettlementDate = table.Column<string>(type: "text", nullable: true),
                    PaymentReference = table.Column<string>(type: "text", nullable: true),
                    ParsedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    ClaimCategory = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RemittanceClaims", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RemittanceClaims_Facilities_FacilityId",
                        column: x => x.FacilityId,
                        principalTable: "Facilities",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_RemittanceClaims_PortalTransactions_RemittanceTransactionId",
                        column: x => x.RemittanceTransactionId,
                        principalTable: "PortalTransactions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "XmlParsedRecords",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PortalTransactionId = table.Column<int>(type: "integer", nullable: false),
                    FacilityId = table.Column<int>(type: "integer", nullable: false),
                    RecordKind = table.Column<string>(type: "text", nullable: false),
                    ClaimId = table.Column<string>(type: "text", nullable: false),
                    FileName = table.Column<string>(type: "text", nullable: true),
                    FileId = table.Column<string>(type: "text", nullable: true),
                    TransactionDate = table.Column<string>(type: "text", nullable: true),
                    SenderId = table.Column<string>(type: "text", nullable: true),
                    ReceiverId = table.Column<string>(type: "text", nullable: true),
                    ReceiverName = table.Column<string>(type: "text", nullable: true),
                    PayerId = table.Column<string>(type: "text", nullable: true),
                    PayerName = table.Column<string>(type: "text", nullable: true),
                    PatientId = table.Column<string>(type: "text", nullable: true),
                    MemberId = table.Column<string>(type: "text", nullable: true),
                    TreatmentDate = table.Column<string>(type: "text", nullable: true),
                    TreatmentDateEnd = table.Column<string>(type: "text", nullable: true),
                    DateOfAdmission = table.Column<string>(type: "text", nullable: true),
                    SubmissionDate = table.Column<string>(type: "text", nullable: true),
                    EncounterType = table.Column<string>(type: "text", nullable: true),
                    Clinician = table.Column<string>(type: "text", nullable: true),
                    ServiceYear = table.Column<string>(type: "text", nullable: true),
                    ServiceMonth = table.Column<string>(type: "text", nullable: true),
                    NetAmount = table.Column<decimal>(type: "numeric", nullable: false),
                    PaidAmount = table.Column<decimal>(type: "numeric", nullable: false),
                    ActivityCount = table.Column<int>(type: "integer", nullable: false),
                    PaymentReference = table.Column<string>(type: "text", nullable: true),
                    SettlementDate = table.Column<string>(type: "text", nullable: true),
                    DenialCodesJson = table.Column<string>(type: "text", nullable: true),
                    Comments = table.Column<string>(type: "text", nullable: true),
                    IdPayer = table.Column<string>(type: "text", nullable: true),
                    ResubmissionType = table.Column<string>(type: "text", nullable: true),
                    PrincipalDiagnosis = table.Column<string>(type: "text", nullable: true),
                    IsMatched = table.Column<bool>(type: "boolean", nullable: false),
                    ReadyForReport = table.Column<bool>(type: "boolean", nullable: false),
                    Notes = table.Column<string>(type: "text", nullable: true),
                    ParsedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    MatchedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_XmlParsedRecords", x => x.Id);
                    table.ForeignKey(
                        name: "FK_XmlParsedRecords_Facilities_FacilityId",
                        column: x => x.FacilityId,
                        principalTable: "Facilities",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_XmlParsedRecords_PortalTransactions_PortalTransactionId",
                        column: x => x.PortalTransactionId,
                        principalTable: "PortalTransactions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ResubmissionTasks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    RemittanceClaimId = table.Column<int>(type: "integer", nullable: false),
                    AssignedToUserId = table.Column<string>(type: "text", nullable: true),
                    AssignedByUserId = table.Column<string>(type: "text", nullable: true),
                    AssignedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    DueDate = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    Status = table.Column<string>(type: "text", nullable: false),
                    Priority = table.Column<string>(type: "text", nullable: false),
                    Notes = table.Column<string>(type: "text", nullable: true),
                    ActionTaken = table.Column<string>(type: "text", nullable: true),
                    StartedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    ResubmittedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    ClosedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ResubmissionTasks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ResubmissionTasks_AspNetUsers_AssignedByUserId",
                        column: x => x.AssignedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_ResubmissionTasks_AspNetUsers_AssignedToUserId",
                        column: x => x.AssignedToUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_ResubmissionTasks_RemittanceClaims_RemittanceClaimId",
                        column: x => x.RemittanceClaimId,
                        principalTable: "RemittanceClaims",
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
                name: "IX_DhpoCodingSets_Category_Code",
                table: "DhpoCodingSets",
                columns: new[] { "Category", "Code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PortalCredentials_FacilityId",
                table: "PortalCredentials",
                column: "FacilityId");

            migrationBuilder.CreateIndex(
                name: "IX_PortalFetchLogs_FacilityId",
                table: "PortalFetchLogs",
                column: "FacilityId");

            migrationBuilder.CreateIndex(
                name: "IX_PortalFetchLogs_FetchedAt",
                table: "PortalFetchLogs",
                column: "FetchedAt");

            migrationBuilder.CreateIndex(
                name: "IX_PortalTransactions_FacilityId_FileDownloaded",
                table: "PortalTransactions",
                columns: new[] { "FacilityId", "FileDownloaded" });

            migrationBuilder.CreateIndex(
                name: "IX_PortalTransactions_FacilityId_FileId",
                table: "PortalTransactions",
                columns: new[] { "FacilityId", "FileId" });

            migrationBuilder.CreateIndex(
                name: "IX_PortalTransactions_Portal_FacilityId_TransactionId",
                table: "PortalTransactions",
                columns: new[] { "Portal", "FacilityId", "TransactionId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RemittanceClaims_ClaimId",
                table: "RemittanceClaims",
                column: "ClaimId");

            migrationBuilder.CreateIndex(
                name: "IX_RemittanceClaims_FacilityId",
                table: "RemittanceClaims",
                column: "FacilityId");

            migrationBuilder.CreateIndex(
                name: "IX_RemittanceClaims_RemittanceTransactionId",
                table: "RemittanceClaims",
                column: "RemittanceTransactionId");

            migrationBuilder.CreateIndex(
                name: "IX_ReportRequests_BranchId",
                table: "ReportRequests",
                column: "BranchId");

            migrationBuilder.CreateIndex(
                name: "IX_ReportRequests_ClinicianId",
                table: "ReportRequests",
                column: "ClinicianId");

            migrationBuilder.CreateIndex(
                name: "IX_ReportRequests_DepartmentId",
                table: "ReportRequests",
                column: "DepartmentId");

            migrationBuilder.CreateIndex(
                name: "IX_ReportRequests_PayerId",
                table: "ReportRequests",
                column: "PayerId");

            migrationBuilder.CreateIndex(
                name: "IX_ReportRequests_ReceiverId",
                table: "ReportRequests",
                column: "ReceiverId");

            migrationBuilder.CreateIndex(
                name: "IX_ResubmissionTasks_AssignedByUserId",
                table: "ResubmissionTasks",
                column: "AssignedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_ResubmissionTasks_AssignedToUserId",
                table: "ResubmissionTasks",
                column: "AssignedToUserId");

            migrationBuilder.CreateIndex(
                name: "IX_ResubmissionTasks_RemittanceClaimId",
                table: "ResubmissionTasks",
                column: "RemittanceClaimId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ResubmissionTasks_Status",
                table: "ResubmissionTasks",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_SystemSettings_Category_Key",
                table: "SystemSettings",
                columns: new[] { "Category", "Key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UserFacilities_FacilityId",
                table: "UserFacilities",
                column: "FacilityId");

            migrationBuilder.CreateIndex(
                name: "IX_UserFacilities_UserId",
                table: "UserFacilities",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_UserReportAccesses_UserId",
                table: "UserReportAccesses",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_XmlParsedRecords_ClaimId",
                table: "XmlParsedRecords",
                column: "ClaimId");

            migrationBuilder.CreateIndex(
                name: "IX_XmlParsedRecords_FacilityId_RecordKind",
                table: "XmlParsedRecords",
                columns: new[] { "FacilityId", "RecordKind" });

            migrationBuilder.CreateIndex(
                name: "IX_XmlParsedRecords_PortalTransactionId",
                table: "XmlParsedRecords",
                column: "PortalTransactionId");

            migrationBuilder.CreateIndex(
                name: "IX_XmlParsedRecords_ReadyForReport",
                table: "XmlParsedRecords",
                column: "ReadyForReport");
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
                name: "DhpoCodingSets");

            migrationBuilder.DropTable(
                name: "PortalCredentials");

            migrationBuilder.DropTable(
                name: "PortalFetchLogs");

            migrationBuilder.DropTable(
                name: "ReportRequests");

            migrationBuilder.DropTable(
                name: "ReportSchedules");

            migrationBuilder.DropTable(
                name: "ResubmissionTasks");

            migrationBuilder.DropTable(
                name: "SystemSettings");

            migrationBuilder.DropTable(
                name: "UserFacilities");

            migrationBuilder.DropTable(
                name: "UserReportAccesses");

            migrationBuilder.DropTable(
                name: "XmlParsedRecords");

            migrationBuilder.DropTable(
                name: "AspNetRoles");

            migrationBuilder.DropTable(
                name: "Clinicians");

            migrationBuilder.DropTable(
                name: "Departments");

            migrationBuilder.DropTable(
                name: "Payers");

            migrationBuilder.DropTable(
                name: "Receivers");

            migrationBuilder.DropTable(
                name: "RemittanceClaims");

            migrationBuilder.DropTable(
                name: "AspNetUsers");

            migrationBuilder.DropTable(
                name: "PortalTransactions");

            migrationBuilder.DropTable(
                name: "Facilities");
        }
    }
}
