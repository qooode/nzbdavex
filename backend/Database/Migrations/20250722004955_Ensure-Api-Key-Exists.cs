using Microsoft.EntityFrameworkCore.Migrations;
using NzbWebDAV.Utils;

#nullable disable

namespace NzbWebDAV.Database.Migrations
{
    /// <inheritdoc />
    public partial class EnsureApiKeyExists : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.InsertData(
                table: "ConfigItems",
                columns: new[] { "ConfigName", "ConfigValue" },
                values: new object[,]
                {
                    {
                        "api.key",
                        GuidUtil.GenerateSecureGuid().ToString("N")
                    },
                }
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Intentionally left blank
        }
    }
}
