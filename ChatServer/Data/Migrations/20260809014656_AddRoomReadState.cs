using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ChatServer.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddRoomReadState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Hand-edited from the scaffolded version, which defaulted to 0001-01-01.
            // That is outside MariaDB's DATETIME range (min 1000-01-01) and would also
            // have made every existing member's entire room history unread on upgrade.
            migrationBuilder.AddColumn<DateTime>(
                name: "LastReadAt",
                table: "ChatRoomMembers",
                type: "datetime(6)",
                nullable: false,
                defaultValue: new DateTime(1970, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc));

            // Existing members start caught up rather than facing a 99+ badge on every room.
            migrationBuilder.Sql("UPDATE `ChatRoomMembers` SET `LastReadAt` = UTC_TIMESTAMP(6);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LastReadAt",
                table: "ChatRoomMembers");
        }
    }
}
