using System.Text.Json;
using DataStore = Atelia.FrameStore.FrameStore;
using Heads = Atelia.HeadStore.HeadStore;

namespace Atelia.HeadStore.Smoke;

/// <summary>A public creation can precede a separate application directory publication.</summary>
internal static class DynamicHeadScenario {
    private const uint DirectoryHead = 1;
    private const uint RegisteredHead = 2;
    private const uint UnregisteredHead = 3;
    private const string RegisteredObject = "business-object/registered";
    private const string UnregisteredObject = "business-object/created-before-registration";
    private const string Checkpoint = "created-before-directory-registration";

    internal static int Run(string[] args) {
        if (args.Length != 2 || args[0] is not ("dynamic-head-worker" or "dynamic-head-read")) {
            throw new ArgumentException("Usage: dynamic-head-worker <fresh-root> | dynamic-head-read <root>");
        }
        string root = Path.GetFullPath(args[1]);
        if (args[0] == "dynamic-head-worker") { Worker(root); }
        else { Read(root); }
        return 0;
    }

    private static void Worker(string root) {
        Require(!File.Exists(root) && (!Directory.Exists(root) || !Directory.EnumerateFileSystemEntries(root).Any()),
            "dynamic-head-worker requires an absent or empty root.");
        Directory.CreateDirectory(root);
        using var data = DataStore.Create(Path.Combine(root, "data"));
        using var heads = Heads.Create(Path.Combine(root, "heads"), data, reservedHeadIdUpperBound: DirectoryHead);
        HeadEntryId registered = heads.CreateHead(ObjectBytes(RegisteredObject), out var outcome).Unwrap();
        Confirmed(outcome);
        Require(registered.HeadId.LocalId == RegisteredHead, "Unexpected first automatic object id.");

        byte[] directoryBytes = JsonSerializer.SerializeToUtf8Bytes(new DirectoryState(1, "directory",
            [new DirectoryEntry(RegisteredObject, RegisteredHead)]));
        HeadEntryId directory = heads.CreateHead(DirectoryHead, directoryBytes, out var directoryOutcome).Unwrap();
        Confirmed(directoryOutcome);
        HeadEntryId created = heads.CreateHead(ObjectBytes(UnregisteredObject), out var createdOutcome).Unwrap();
        Confirmed(createdOutcome);
        Require(created.HeadId.LocalId == UnregisteredHead, "Unexpected second automatic object id.");

        var manifest = new Manifest(Convert.ToBase64String(data.StoreId), Convert.ToBase64String(heads.HeadStoreId),
            directory.Ticket.Packed, created.Ticket.Packed, Convert.ToBase64String(directoryBytes));
        // Fixture evidence only: it is not an application directory or a redo protocol.
        File.WriteAllText(Path.Combine(root, "dynamic-head-checkpoint.json"), JsonSerializer.Serialize(manifest));
        Summary summary = Validate(heads, manifest);
        Emit(new {
            kind = "ready", command = "dynamic-head-worker", pid = Environment.ProcessId, checkpoint = Checkpoint,
            createdOutcome = createdOutcome.ToString(), directoryOutcome = directoryOutcome.ToString(),
            directoryPublicationAfterCreation = false, dataOpened = true, headsOpened = true,
            summary.DirectoryHeadId, summary.DirectoryTicket, summary.CreatedHeadId, summary.CreatedTicket,
            summary.RegisteredHeadIds, summary.UnregisteredHeadIds, summary.UnregisteredObjectId,
            publicBoundary = "after-create-confirmed-before-directory-publish"
        });
        // These owners stay alive. There is no release or successful managed exit.
        string? input = Console.ReadLine();
        throw new InvalidOperationException(input is null
            ? "dynamic-head-worker reached EOF before its parent killed it."
            : "dynamic-head-worker must be killed, not released through stdin.");
    }

    private static void Read(string root) {
        Manifest manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(Path.Combine(root, "dynamic-head-checkpoint.json")))
            ?? throw new InvalidDataException("Missing dynamic ref fixture evidence.");
        Summary summary;
        HeadStoreAudit audit;
        using (var data = DataStore.OpenReadOnly(Path.Combine(root, "data"))) {
            using var heads = Heads.OpenReadOnly(Path.Combine(root, "heads"), data);
            Require(data.IsReadOnly && heads.IsReadOnly, "The cold consumer must use read-only owners.");
            Require(Convert.ToBase64String(data.StoreId) == manifest.DataId
                && Convert.ToBase64String(heads.HeadStoreId) == manifest.HeadStoreId, "Cold store identity changed.");
            summary = Validate(heads, manifest);
            audit = heads.Audit().Unwrap();
            Require(audit.HeadCount == 3 && audit.EntryCount == 3 && audit.OriginCount == 0
                && audit.TagCount == 0, "Unexpected dynamic ref physical inventory.");
        }
        Emit(new {
            kind = "completed", status = "ok", command = "dynamic-head-read", pid = Environment.ProcessId,
            checkpoint = Checkpoint, summary.DirectoryHeadId, summary.DirectoryTicket, summary.CreatedHeadId,
            summary.CreatedTicket, summary.RegisteredHeadIds, summary.UnregisteredHeadIds, summary.UnregisteredObjectId,
            directoryOldValue = true, businessIdentityChecked = true, audit.HeadCount, audit.EntryCount
        });
    }

    private static Summary Validate(Heads heads, Manifest manifest) {
        uint[] discovered = heads.ListHeads().Unwrap().Select(id => id.LocalId).Order().ToArray();
        Require(discovered.SequenceEqual(new uint[] { DirectoryHead, RegisteredHead, UnregisteredHead }),
            "ListHeads must discover the confirmed object independently of the application directory.");
        HeadEntry directory = heads.ReadHead(DirectoryHead).Unwrap();
        Require(directory.Id.Ticket.Packed == manifest.DirectoryTicket
            && directory.Payload.SequenceEqual(Convert.FromBase64String(manifest.DirectoryPayload)),
            "The directory advanced across the creation-only checkpoint.");
        DirectoryState state = JsonSerializer.Deserialize<DirectoryState>(directory.Payload)
            ?? throw new InvalidDataException("Missing application directory state.");
        Require(state.Schema == 1 && state.Kind == "directory" && state.Entries.Length == 1
            && state.Entries[0] == new DirectoryEntry(RegisteredObject, RegisteredHead), "The old directory value changed.");

        var objects = new Dictionary<uint, string>();
        foreach (uint localId in discovered.Where(id => id != DirectoryHead)) {
            HeadEntry entry = heads.ReadHead(localId).Unwrap();
            ObjectState item = JsonSerializer.Deserialize<ObjectState>(entry.Payload)
                ?? throw new InvalidDataException("Missing discovered business object state.");
            string expectedObject = localId == RegisteredHead ? RegisteredObject : UnregisteredObject;
            Require(item.Schema == 1 && item.Kind == "object" && item.ObjectId == expectedObject && item.Value == "complete-object-state"
                && entry.Payload.SequenceEqual(ObjectBytes(expectedObject)), "The discovered object has the wrong business identity or bytes.");
            if (localId == UnregisteredHead) {
                Require(entry.Id.Ticket.Packed == manifest.CreatedTicket, "The created object's exact entryId changed.");
            }
            objects.Add(localId, item.ObjectId);
        }
        uint[] registered = state.Entries.Select(entry => entry.HeadLocalId).ToArray();
        uint[] unregistered = objects.Keys.Except(registered).Order().ToArray();
        Require(unregistered.SequenceEqual(new uint[] { UnregisteredHead })
            && objects[UnregisteredHead] == UnregisteredObject, "The directory incorrectly registered the newly created object.");
        return new Summary(DirectoryHead, directory.Id.Ticket.Packed, UnregisteredHead, manifest.CreatedTicket,
            registered, unregistered, objects[UnregisteredHead]);
    }

    private static byte[] ObjectBytes(string objectId) => JsonSerializer.SerializeToUtf8Bytes(
        new ObjectState(1, "object", objectId, "complete-object-state"));
    private static void Confirmed(PublicationOutcome outcome) => Require(outcome == PublicationOutcome.Confirmed, "Public creation did not confirm.");
    private static void Emit(object message) {
        Console.WriteLine(JsonSerializer.Serialize(message));
        Console.Out.Flush();
    }
    private static void Require(bool condition, string message) {
        if (!condition) { throw new InvalidDataException(message); }
    }
    private sealed record DirectoryEntry(string ObjectId, uint HeadLocalId);
    private sealed record DirectoryState(int Schema, string Kind, DirectoryEntry[] Entries);
    private sealed record ObjectState(int Schema, string Kind, string ObjectId, string Value);
    private sealed record Manifest(string DataId, string HeadStoreId, ulong DirectoryTicket, ulong CreatedTicket, string DirectoryPayload);
    private sealed record Summary(uint DirectoryHeadId, ulong DirectoryTicket, uint CreatedHeadId, ulong CreatedTicket,
        uint[] RegisteredHeadIds, uint[] UnregisteredHeadIds, string UnregisteredObjectId);
}
