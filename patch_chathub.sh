#!/usr/bin/env bash
# Applies the targeted patch to ChatServer/Hubs/ChatHubImpl.cs
# Run from the repo root.

set -e

FILE="ChatServer/Hubs/ChatHubImpl.cs"

# Fix 1: Replace the session/handle creation + room ID derivation block.
# Old code used Math.Abs(roomId.GetHashCode()) — only 32-bit, high collision risk.
# New code XORs two 32-bit slices of the Guid bytes for a stable, wider hash.
# Old code passed handleId to EnsureVideoRoomAsync — new signature doesn't need it.

python3 - "$FILE" << 'PYEOF'
import sys, re

path = sys.argv[1]
with open(path, 'r') as f:
    src = f.read()

old = (
    "        var sessionId = await _janus.CreateSessionAsync();\n"
    "        var handleId = await _janus.AttachPluginAsync(sessionId);\n"
    "\n"
    "        long janusRoomId = Math.Abs(roomId.GetHashCode());\n"
    "        if (janusRoomId == 0) janusRoomId = 1;\n"
    "\n"
    "        await _janus.EnsureVideoRoomAsync(sessionId, handleId, janusRoomId);"
)

new = (
    "        var sessionId = await _janus.CreateSessionAsync();\n"
    "        var handleId = await _janus.AttachPluginAsync(sessionId);\n"
    "\n"
    "        // Derive a stable 32-bit positive room ID from the Guid bytes.\n"
    "        // XOR two 32-bit windows so all 16 bytes contribute.\n"
    "        var guidBytes = roomId.ToByteArray();\n"
    "        long janusRoomId = Math.Abs(\n"
    "            (long)(BitConverter.ToInt32(guidBytes, 0) ^ BitConverter.ToInt32(guidBytes, 4)));\n"
    "        if (janusRoomId == 0) janusRoomId = 1;\n"
    "\n"
    "        // EnsureVideoRoom uses its own temporary handle internally,\n"
    "        // so the publisher handle returned to the client is clean.\n"
    "        await _janus.EnsureVideoRoomAsync(sessionId, janusRoomId);"
)

if old not in src:
    print("ERROR: Could not find the target block in", path)
    print("You may need to apply this patch manually.")
    sys.exit(1)

src = src.replace(old, new, 1)
with open(path, 'w') as f:
    f.write(src)

print("Patched", path, "successfully.")
PYEOF
