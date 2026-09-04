using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Analytika.Migrations;

public partial class AddReportMultiSelectFilters : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>("FacilityIdsCsv", "ReportRequests", type: "text", nullable: true);
        migrationBuilder.AddColumn<string>("ReceiverIdsCsv", "ReportRequests", type: "text", nullable: true);
        migrationBuilder.AddColumn<string>("PayerIdsCsv", "ReportRequests", type: "text", nullable: true);
        migrationBuilder.AddColumn<string>("ClinicianIdsCsv", "ReportRequests", type: "text", nullable: true);
        migrationBuilder.AddColumn<string>("DepartmentIdsCsv", "ReportRequests", type: "text", nullable: true);
        migrationBuilder.AddColumn<string>("EncounterTypesCsv", "ReportRequests", type: "text", nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn("FacilityIdsCsv", "ReportRequests");
        migrationBuilder.DropColumn("ReceiverIdsCsv", "ReportRequests");
        migrationBuilder.DropColumn("PayerIdsCsv", "ReportRequests");
        migrationBuilder.DropColumn("ClinicianIdsCsv", "ReportRequests");
        migrationBuilder.DropColumn("DepartmentIdsCsv", "ReportRequests");
        migrationBuilder.DropColumn("EncounterTypesCsv", "ReportRequests");
    }
}
