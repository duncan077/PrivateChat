-- Create a chat room directly in MariaDB.
--
-- Mirrors exactly what ChatRoomServiceImpl.CreateRoomAsync does: one ChatRooms row
-- plus a ChatRoomMembers row for the owner. Skipping the membership row would
-- create a room its own owner is not in, which the client filters out of the room
-- list - so both inserts matter.
--
-- Usage:
--   mariadb -u chatflow -p chatflow < tools/sql/create-room.sql
--
-- Afterwards drop the cached room list (see the note at the bottom), or the API
-- keeps serving the old one for up to 2 minutes.

-- ---------------------------------------------------------------- parameters --
SET @UserName        := 'duncan088';      -- room owner (must already exist)
SET @RoomName        := 'General';
SET @RoomDescription := 'General chat';
SET @VoiceEnabled    := 1;                -- tinyint(1): 1 = on, 0 = off
SET @VideoEnabled    := 1;
-- -------------------------------------------------------------------------------

-- UUID() yields the lowercase 36-char form that char(36) columns and .NET Guid
-- both expect.
SET @RoomId   := UUID();
SET @MemberId := UUID();

START TRANSACTION;

-- INSERT ... SELECT rather than VALUES: if the username is wrong this inserts
-- nothing instead of failing on a foreign key with a cryptic message.
INSERT INTO `ChatRooms`
    (`Id`, `Name`, `Description`, `OwnerId`, `IsVoiceEnabled`, `IsVideoEnabled`, `CreatedAt`)
SELECT
    @RoomId, @RoomName, @RoomDescription, u.`Id`, @VoiceEnabled, @VideoEnabled, UTC_TIMESTAMP(6)
FROM `AspNetUsers` u
WHERE u.`UserName` = @UserName;

-- Role 0 = Member. CreateRoomAsync uses UserRole.Member for the creator too;
-- room-level authority comes from ChatRooms.OwnerId, not this column.
INSERT INTO `ChatRoomMembers`
    (`Id`, `RoomId`, `UserId`, `Role`, `JoinedAt`)
SELECT
    @MemberId, @RoomId, u.`Id`, 0, UTC_TIMESTAMP(6)
FROM `AspNetUsers` u
WHERE u.`UserName` = @UserName
  AND EXISTS (SELECT 1 FROM `ChatRooms` r WHERE r.`Id` = @RoomId);

COMMIT;

-- Result: 1 row means success; 0 rows means @UserName did not match any account.
SELECT
    r.`Id`            AS RoomId,
    r.`Name`          AS RoomName,
    u.`UserName`      AS Owner,
    r.`IsVoiceEnabled`,
    r.`IsVideoEnabled`,
    (SELECT COUNT(*) FROM `ChatRoomMembers` m WHERE m.`RoomId` = r.`Id`) AS Members
FROM `ChatRooms` r
JOIN `AspNetUsers` u ON u.`Id` = r.`OwnerId`
WHERE r.`Id` = @RoomId;

-- The room list is cached in Redis under "PrivateChat:rooms:all" for 2 minutes
-- (ChatRoomServiceImpl.GetRoomsAsync). Clear it so the room shows up immediately:
--
--   redis-cli DEL "PrivateChat:rooms:all"
--
-- No Redis configured? Then there is no cache and the room appears on next load.
