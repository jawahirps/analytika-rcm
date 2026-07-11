using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Analytika.Migrations
{
    /// <inheritdoc />
    public partial class AddXmlGapsFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ClaimCategory",
                table: "XmlParsedRecords",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DiagnosesJson",
                table: "XmlParsedRecords",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "GrossAmount",
                table: "XmlParsedRecords",
                type: "numeric",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<string>(
                name: "PatientDob",
                table: "XmlParsedRecords",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PatientGender",
                table: "XmlParsedRecords",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PatientNationalId",
                table: "XmlParsedRecords",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "XmlParsedActivities",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    XmlParsedRecordId = table.Column<int>(type: "integer", nullable: false),
                    ActivityCode = table.Column<string>(type: "text", nullable: true),
                    ActivityType = table.Column<string>(type: "text", nullable: true),
                    Quantity = table.Column<decimal>(type: "numeric", nullable: false),
                    Net = table.Column<decimal>(type: "numeric", nullable: false),
                    Gross = table.Column<decimal>(type: "numeric", nullable: false),
                    PaymentAmount = table.Column<decimal>(type: "numeric", nullable: false),
                    DenialCode = table.Column<string>(type: "text", nullable: true),
                    Clinician = table.Column<string>(type: "text", nullable: true),
                    Start = table.Column<string>(type: "text", nullable: true),
                    OrderingClinician = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_XmlParsedActivities", x => x.Id);
                    table.ForeignKey(
                        name: "FK_XmlParsedActivities_XmlParsedRecords_XmlParsedRecordId",
                        column: x => x.XmlParsedRecordId,
                        principalTable: "XmlParsedRecords",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_XmlParsedActivities_XmlParsedRecordId",
                table: "XmlParsedActivities",
                column: "XmlParsedRecordId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "XmlParsedActivities");

            migrationBuilder.DropColumn(
                name: "ClaimCategory",
                table: "XmlParsedRecords");

            migrationBuilder.DropColumn(
                name: "DiagnosesJson",
                table: "XmlParsedRecords");

            migrationBuilder.DropColumn(
                name: "GrossAmount",
                table: "XmlParsedRecords");

            migrationBuilder.DropColumn(
                name: "PatientDob",
                table: "XmlParsedRecords");

            migrationBuilder.DropColumn(
                name: "PatientGender",
                table: "XmlParsedRecords");

            migrationBuilder.DropColumn(
                name: "PatientNationalId",
                table: "XmlParsedRecords");
        }
    }
}
