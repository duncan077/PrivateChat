using System.Text.Json.Serialization;
using Shared.Models;

namespace ChatServer.Serialization;

/// <summary>
/// Compile-time JSON contracts for everything the server serializes itself
/// (distributed-cache payloads and the mention lists stored on message rows).
///
/// Using a source-generated context instead of reflection removes the per-call
/// metadata walk and keeps these paths trim/AOT-clean.
///
/// The naming policy is deliberately left at the default: cached entries written
/// by an older build must still deserialize after a deploy.
/// </summary>
[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Default)]
[JsonSerializable(typeof(ChatRoomInfo[]))]
[JsonSerializable(typeof(ChatMessage[]))]
[JsonSerializable(typeof(ServerMemberInfo[]))]
[JsonSerializable(typeof(Guid[]))]
internal sealed partial class ServerJsonContext : JsonSerializerContext
{
}
