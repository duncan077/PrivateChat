using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ChatServer.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddMessageRoomSentAtIndex : Migration
    {
        // Create-then-drop in both directions, which is not the order EF scaffolded.
        // Messages.RoomId carries a foreign key to ChatRooms, and MariaDB refuses to
        // drop the last index covering an FK column ("needed in a foreign key
        // constraint", errno 1553). Creating the replacement first means RoomId is
        // always covered by something - the new index leads with RoomId, so it serves
        // the constraint once IX_Messages_RoomId is gone.

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_Messages_RoomId_IsDeleted_SentAt",
                table: "Messages",
                columns: new[] { "RoomId", "IsDeleted", "SentAt" });

            migrationBuilder.DropIndex(
                name: "IX_Messages_RoomId",
                table: "Messages");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_Messages_RoomId",
                table: "Messages",
                column: "RoomId");

            migrationBuilder.DropIndex(
                name: "IX_Messages_RoomId_IsDeleted_SentAt",
                table: "Messages");
        }
    }
}
