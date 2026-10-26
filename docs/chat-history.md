# Blazor chat history

The client is `ChatAppBlazor` (.NET MAUI Blazor Hybrid).

- Room and DM stores retain their message list and existing message objects. An ID set deduplicates page results and live arrivals; new rows are inserted in timestamp/ID order. Delayed pages do not overwrite newer live edits or reactions.
- `VirtualMessageList` renders a measured window with spacers. Row heights are tracked by message ID, including attachment and preview resizing. It preserves the first visible message across prepends and follows new messages only while the reader is at the bottom. Link previews are requested for rendered rows only.
- Reconnect recovery starts from the position captured at disconnect, fetches forward in bounded pages, and preserves loaded history. Selection/reconnect generations discard obsolete responses. A second disconnect retains the original recovery boundary.
- Room unread counts and mention flags are returned by one grouped query, including memberships with no unread messages. Existing JSON mention storage is unchanged.
- New room/DM page RPCs use `(SentAt, Id)` cursors. Before pages are newest-first; after pages are oldest-first. Only one direction is accepted. Reply-context windows use the same ordering.

Deploy the server before the updated Blazor client. Existing timestamp-only RPCs remain available for older clients. No schema migration is required.

## Verification

Run `dotnet test ChatApp.Tests/ChatApp.Tests.csproj`. Tests cover multi-page reconnect gaps, duplicate live arrivals, obsolete responses, equal timestamps in both history directions, production-provider SQL translation, and unread filtering/capping.

Browser verification used the actual virtual-list component in an isolated local Blazor host with 1,000 variable-height messages. About 20 rows were rendered in the tested middle viewport. Prepending 50 messages, appending a message while reading history, and growing visible rows preserved the first visible message and its pixel offset. Latest-message following, scrolling to the oldest loaded message, and conversation replacement were also checked.

This is rendering virtualization: loaded message data remains in memory, and older history is fetched through the existing load-older control. Recovery fetches newly sent messages; it does not provide an edit/delete event log for older messages changed while offline.
