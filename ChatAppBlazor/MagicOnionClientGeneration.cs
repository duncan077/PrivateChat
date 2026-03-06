using MagicOnion.Client;
using Shared.Services;

[MagicOnionClientGeneration(
    typeof(IAuthService),
    typeof(IChatRoomService),
    typeof(IFileUploadService),
    typeof(IPrivateMessageService))]
internal partial class MagicOnionClientGenerationMarker
{
}
