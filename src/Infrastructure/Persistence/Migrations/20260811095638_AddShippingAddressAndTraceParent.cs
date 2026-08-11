using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KartOrderService.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddShippingAddressAndTraceParent : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "shipping_city",
                table: "orders",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "shipping_country",
                table: "orders",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "shipping_line1",
                table: "orders",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "shipping_line2",
                table: "orders",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "shipping_phone",
                table: "orders",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "shipping_postal_code",
                table: "orders",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "shipping_recipient_name",
                table: "orders",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "shipping_state",
                table: "orders",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "trace_parent",
                table: "order_events",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "shipping_city",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "shipping_country",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "shipping_line1",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "shipping_line2",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "shipping_phone",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "shipping_postal_code",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "shipping_recipient_name",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "shipping_state",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "trace_parent",
                table: "order_events");
        }
    }
}
