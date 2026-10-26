using MagicOnion.Client;
using Shared.Hubs;
using Shared.Services;

// Every unary service must be listed here: the source generator only emits clients for
// these, and trimmed/AOT Android builds fail at runtime for anything missing.
//
// IChatHub is listed so the StreamingHub client is source-generated too. Without it,
// StreamingHubClient.ConnectAsync builds the proxy with Reflection.Emit on every
// connect — slower first connect, and a hard blocker for any future trimming/AOT.
[MagicOnionClientGeneration(
    typeof(IAuthService),
    typeof(IChatRoomService),
    typeof(IFileUploadService),
    typeof(IPrivateMessageService),
    typeof(IServerAssetService),
    typeof(IChatHub))]
internal partial class MagicOnionClientGenerationMarker
{
}
