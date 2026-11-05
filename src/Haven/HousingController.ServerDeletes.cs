using Haven.Core;

namespace Haven;

/// <summary>Haven's server refused a delete because another character uploaded the file: trying again never helps.</summary>
internal sealed class NotUploaderException(string message) : InvalidOperationException(message);

// Server deletes that failed (PendingDeletes): the file is gone from this PC already, and Haven deletes its server copy
// later, as the character that uploaded it, trying again with a growing wait. 0.53.x deletes made just after a relay
// update were refused for the version and simply lost; now they wait for the next chance.
internal sealed partial class HousingController
{
    private PendingDeletes? pendingDeletes;
    private PendingDeletes PendingServerDeletes => pendingDeletes ??= new(Path.Combine(Path.GetDirectoryName(Store.DirectoryPath)!, "content", "pending-deletes.json"));
    private DateTimeOffset nextDeleteCheck;
    private volatile bool deletingLater;

    // A delete from the server that failed for a reason that may pass (not the wrong character's upload).
    private static bool Retryable(Exception ex) => ex is not NotUploaderException
        && ex is IOException or InvalidOperationException or HttpRequestException or TaskCanceledException or System.Net.WebSockets.WebSocketException
            or System.Text.Json.JsonException;   // a ticket the relay sent garbled

    private void QueueServerDelete(ServerContent kind, string hash, ulong character, Exception reason)
    {
        log.Warning("[CONTENT] {Kind} {Hash}: the server copy will be deleted later ({Reason}).", kind, hash, reason.Message);
        PendingServerDeletes.Add(kind, hash, HomeOwners.Key(character), DateTimeOffset.UtcNow);
    }

    // Each minute or so: the owed deletes of the character logged in now, one session for all of them.
    private void UpdateServerDeletes()
    {
        var now = DateTimeOffset.UtcNow;
        if (deletingLater || now < nextDeleteCheck) return;
        nextDeleteCheck = now.AddSeconds(30);
        var character = engine.HousingCharacterId;
        if (character == 0 || PendingServerDeletes.Due(HomeOwners.Key(character), now) is not { Count: > 0 } due) return;
        var relayUrl = engine.HousingRelayUrl;
        deletingLater = true;
        _ = Task.Run(async () =>
        {
            try
            {
                await using var session = await ContentSessionClient.Open(relayUrl, character, pictures.SecretFor(character), CancellationToken.None, o => Account.Apply(o, character, ""));
                foreach (var entry in due)
                {
                    try
                    {
                        var delete = entry.Kind switch
                        {
                            ServerContent.Picture => pictures.DeleteFromServer(session.Request, session.Origin, entry.Hash, CancellationToken.None),
                            ServerContent.Song => Songs.DeleteFromServer(session.Request, session.Origin, entry.Hash, CancellationToken.None),
                            ServerContent.Furniture => Furniture.DeleteFromServer(session.Request, session.Origin, entry.Hash, CancellationToken.None),
                            _ => Animations.DeleteFromServer(session.Request, session.Origin, entry.Hash, CancellationToken.None),
                        };
                        await delete;
                        log.Information("[CONTENT] {Kind} {Hash}: the server copy was deleted after all.", entry.Kind, entry.Hash);
                        Run(() => PendingServerDeletes.Done(entry));
                    }
                    catch (NotUploaderException) { Run(() => PendingServerDeletes.Done(entry)); }
                    catch (Exception ex)
                    {
                        // Anything else waits longer too (and is said), so one odd failure can't skip the rest unlogged.
                        if (!Retryable(ex)) log.Warning("[CONTENT] {Kind} {Hash}: deleting the server copy failed ({Message}); tried again later.", entry.Kind, entry.Hash, ex.Message);
                        Run(() => PendingServerDeletes.Failed(entry, DateTimeOffset.UtcNow));
                    }
                }
            }
            catch (Exception ex)
            {
                // No session (offline, signed out, an older relay): every one of them waits longer.
                if (!Retryable(ex)) log.Warning("[CONTENT] server deletes waiting: {Message}; tried again later.", ex.Message);
                Run(() => { foreach (var entry in due) PendingServerDeletes.Failed(entry, DateTimeOffset.UtcNow); });
            }
            finally { deletingLater = false; }
        });
    }
}
