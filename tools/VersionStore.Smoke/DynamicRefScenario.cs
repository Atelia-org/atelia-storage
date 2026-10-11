using System.Text.Json;
using DataStore = Atelia.FrameStore.FrameStore;
using Versions = Atelia.VersionStore.VersionStore;

namespace Atelia.VersionStore.Smoke;

/// <summary>A public creation can precede a separate application directory publication.</summary>
internal static class DynamicRefScenario {
    private const uint DirectoryRef = 1;
    private const uint RegisteredRef = 2;
    private const uint UnregisteredRef = 3;
    private const string RegisteredObject = "business-object/registered";
    private const string UnregisteredObject = "business-object/created-before-registration";
    private const string Checkpoint = "created-before-directory-registration";

    internal static int Run(string[] args) {
        if (args.Length != 2 || args[0] is not ("dynamic-ref-worker" or "dynamic-ref-read")) {
            throw new ArgumentException("Usage: dynamic-ref-worker <fresh-root> | dynamic-ref-read <root>");
        }
        string root = Path.GetFullPath(args[1]);
        if (args[0] == "dynamic-ref-worker") { Worker(root); }
        else { Read(root); }
        return 0;
    }

    private static void Worker(string root) {
        Require(!File.Exists(root) && (!Directory.Exists(root) || !Directory.EnumerateFileSystemEntries(root).Any()),
            "dynamic-ref-worker requires an absent or empty root.");
        Directory.CreateDirectory(root);
        using var data = DataStore.Create(Path.Combine(root, "data"));
        using var versions = Versions.Create(Path.Combine(root, "versions"), data, reservedRefIdUpperBound: DirectoryRef);
        RefRevision registered = versions.CreateRef(ObjectBytes(RegisteredObject), out var outcome).Unwrap();
        Confirmed(outcome);
        Require(registered.RefId.LocalId == RegisteredRef, "Unexpected first automatic object id.");

        byte[] directoryBytes = JsonSerializer.SerializeToUtf8Bytes(new DirectoryState(1, "directory",
            [new DirectoryEntry(RegisteredObject, RegisteredRef)]));
        RefRevision directory = versions.CreateRef(DirectoryRef, directoryBytes, out var directoryOutcome).Unwrap();
        Confirmed(directoryOutcome);
        RefRevision created = versions.CreateRef(ObjectBytes(UnregisteredObject), out var createdOutcome).Unwrap();
        Confirmed(createdOutcome);
        Require(created.RefId.LocalId == UnregisteredRef, "Unexpected second automatic object id.");

        var manifest = new Manifest(Convert.ToBase64String(data.StoreId), Convert.ToBase64String(versions.VersionStoreId),
            directory.Ticket.Packed, created.Ticket.Packed, Convert.ToBase64String(directoryBytes));
        // Fixture evidence only: it is not an application directory or a redo protocol.
        File.WriteAllText(Path.Combine(root, "dynamic-ref-checkpoint.json"), JsonSerializer.Serialize(manifest));
        Summary summary = Validate(versions, manifest);
        Emit(new {
            kind = "ready", command = "dynamic-ref-worker", pid = Environment.ProcessId, checkpoint = Checkpoint,
            createdOutcome = createdOutcome.ToString(), directoryOutcome = directoryOutcome.ToString(),
            directoryPublicationAfterCreation = false, dataOpened = true, versionsOpened = true,
            summary.DirectoryRefId, summary.DirectoryTicket, summary.CreatedRefId, summary.CreatedTicket,
            summary.RegisteredRefIds, summary.UnregisteredRefIds, summary.UnregisteredObjectId,
            publicBoundary = "after-create-confirmed-before-directory-publish"
        });
        // These owners stay alive. There is no release or successful managed exit.
        string? input = Console.ReadLine();
        throw new InvalidOperationException(input is null
            ? "dynamic-ref-worker reached EOF before its parent killed it."
            : "dynamic-ref-worker must be killed, not released through stdin.");
    }

    private static void Read(string root) {
        Manifest manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(Path.Combine(root, "dynamic-ref-checkpoint.json")))
            ?? throw new InvalidDataException("Missing dynamic ref fixture evidence.");
        Summary summary;
        VersionStoreAudit audit;
        using (var data = DataStore.OpenReadOnly(Path.Combine(root, "data"))) {
            using var versions = Versions.OpenReadOnly(Path.Combine(root, "versions"), data);
            Require(data.IsReadOnly && versions.IsReadOnly, "The cold consumer must use read-only owners.");
            Require(Convert.ToBase64String(data.StoreId) == manifest.DataId
                && Convert.ToBase64String(versions.VersionStoreId) == manifest.VersionStoreId, "Cold store identity changed.");
            summary = Validate(versions, manifest);
            audit = versions.Audit().Unwrap();
            Require(audit.RefCount == 3 && audit.SnapshotCount == 3 && audit.ForkCount == 0
                && audit.TagCount == 0, "Unexpected dynamic ref physical inventory.");
        }
        Emit(new {
            kind = "completed", status = "ok", command = "dynamic-ref-read", pid = Environment.ProcessId,
            checkpoint = Checkpoint, summary.DirectoryRefId, summary.DirectoryTicket, summary.CreatedRefId,
            summary.CreatedTicket, summary.RegisteredRefIds, summary.UnregisteredRefIds, summary.UnregisteredObjectId,
            directoryOldValue = true, businessIdentityChecked = true, audit.RefCount, audit.SnapshotCount
        });
    }

    private static Summary Validate(Versions versions, Manifest manifest) {
        uint[] discovered = versions.ListRefs().Unwrap().Select(id => id.LocalId).Order().ToArray();
        Require(discovered.SequenceEqual(new uint[] { DirectoryRef, RegisteredRef, UnregisteredRef }),
            "ListRefs must discover the confirmed object independently of the application directory.");
        RefBytesSnapshot directory = versions.ReadRef(DirectoryRef).Unwrap();
        Require(directory.Revision.Ticket.Packed == manifest.DirectoryTicket
            && directory.Payload.SequenceEqual(Convert.FromBase64String(manifest.DirectoryPayload)),
            "The directory advanced across the creation-only checkpoint.");
        DirectoryState state = JsonSerializer.Deserialize<DirectoryState>(directory.Payload)
            ?? throw new InvalidDataException("Missing application directory state.");
        Require(state.Schema == 1 && state.Kind == "directory" && state.Entries.Length == 1
            && state.Entries[0] == new DirectoryEntry(RegisteredObject, RegisteredRef), "The old directory value changed.");

        var objects = new Dictionary<uint, string>();
        foreach (uint localId in discovered.Where(id => id != DirectoryRef)) {
            RefBytesSnapshot snapshot = versions.ReadRef(localId).Unwrap();
            ObjectState item = JsonSerializer.Deserialize<ObjectState>(snapshot.Payload)
                ?? throw new InvalidDataException("Missing discovered business object state.");
            string expectedObject = localId == RegisteredRef ? RegisteredObject : UnregisteredObject;
            Require(item.Schema == 1 && item.Kind == "object" && item.ObjectId == expectedObject && item.Value == "complete-object-state"
                && snapshot.Payload.SequenceEqual(ObjectBytes(expectedObject)), "The discovered object has the wrong business identity or bytes.");
            if (localId == UnregisteredRef) {
                Require(snapshot.Revision.Ticket.Packed == manifest.CreatedTicket, "The created object's exact revision changed.");
            }
            objects.Add(localId, item.ObjectId);
        }
        uint[] registered = state.Entries.Select(entry => entry.RefLocalId).ToArray();
        uint[] unregistered = objects.Keys.Except(registered).Order().ToArray();
        Require(unregistered.SequenceEqual(new uint[] { UnregisteredRef })
            && objects[UnregisteredRef] == UnregisteredObject, "The directory incorrectly registered the newly created object.");
        return new Summary(DirectoryRef, directory.Revision.Ticket.Packed, UnregisteredRef, manifest.CreatedTicket,
            registered, unregistered, objects[UnregisteredRef]);
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
    private sealed record DirectoryEntry(string ObjectId, uint RefLocalId);
    private sealed record DirectoryState(int Schema, string Kind, DirectoryEntry[] Entries);
    private sealed record ObjectState(int Schema, string Kind, string ObjectId, string Value);
    private sealed record Manifest(string DataId, string VersionStoreId, ulong DirectoryTicket, ulong CreatedTicket, string DirectoryPayload);
    private sealed record Summary(uint DirectoryRefId, ulong DirectoryTicket, uint CreatedRefId, ulong CreatedTicket,
        uint[] RegisteredRefIds, uint[] UnregisteredRefIds, string UnregisteredObjectId);
}
