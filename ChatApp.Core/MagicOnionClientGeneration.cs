using MagicOnion.Client;
using Shared.Services;

/// <summary>
/// Drives MagicOnion's source generator so gRPC clients are emitted at compile time
/// instead of via runtime reflection. Required for trimmed and AOT-compiled Android
/// builds; harmless on desktop.
/// </summary>
[MagicOnionClientGeneration(
    typeof(IAuthService),
    typeof(IChatRoomService),
    typeof(IFileUploadService),
    typeof(IPrivateMessageService))]
internal partial class MagicOnionClientGenerationMarker
{
}
