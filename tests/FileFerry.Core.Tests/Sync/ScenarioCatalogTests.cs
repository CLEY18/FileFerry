using FileFerry.Core.Domain;
using FileFerry.Core.Domain.Paths;
using FileFerry.Core.Sync;
using FileFerry.Core.Tests.Sync;

namespace FileFerry.Core.Tests.Sync;

/// <summary>
/// Executable merge: every scenario is a line of "living documentation" on how
/// the three sides (local/remote/baseline) turn into ordered operations.
/// A new merge bug ⟶ a new scenario here BEFORE the fix (scenario-first).
/// </summary>
[Trait("Category", "SyncScenario")]
public sealed class ScenarioCatalogTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly FolderPair Pair = new(Guid.Empty, Guid.Empty, "Sync", "tree", "Sync");

    [Fact]
    public void Delete_local_propagates_deletion_to_remote_through_stash()
    {
        var plan = Plan(
            local: [],
            remote: [File("a.txt", etag: "e1")],
            baseline: [File("a.txt", etag: "e1")]);

        Assert.Collection(plan.Operations,
            op => Assert.Equal(OperationKind.DownloadToLocal, op.Kind), // copy from the server into the stash
            op => Assert.Equal(OperationKind.DeleteRemote, op.Kind));
        Assert.Empty(plan.Conflicts);
    }

    [Fact]
    public void Delete_remote_propagates_deletion_to_local_through_stash()
    {
        var plan = Plan(
            local: [File("a.txt", etag: "e1")],
            remote: [],
            baseline: [File("a.txt", etag: "e1")]);

        Assert.Collection(plan.Operations,
            op => Assert.Equal(OperationKind.CopyLocal, op.Kind), // copy of the local file into the stash
            op => Assert.Equal(OperationKind.DeleteLocal, op.Kind));
        Assert.StartsWith("FileFerry Trash/", plan.Operations[0].Path.ToString());
        Assert.Empty(plan.Conflicts);
    }

    [Fact]
    public void Stash_buffer_is_invisible_to_the_planner()
    {
        var plan = Plan(
            local: [File("a.txt", etag: "e1"), File("FileFerry Trash/20260101T000000/a.txt", etag: "e1")],
            remote: [],
            baseline: [File("a.txt", etag: "e1")]);

        // A stash copy is not a "local addition": the plan stays paired (copy+delete) and never touches the buffer
        Assert.Collection(plan.Operations,
            op => Assert.Equal(OperationKind.CopyLocal, op.Kind),
            op => Assert.Equal(OperationKind.DeleteLocal, op.Kind));
        Assert.DoesNotContain(plan.Operations, static op => op.SourcePath?.ToString().StartsWith("FileFerry Trash/") ?? false);
    }

    [Fact]
    public void Default_stash_root_is_writable_through_saf()
    {
        // Emulator (Android 16): the buffer name has no dot prefix — dot documents are SAF
        // provider-defined. The real blocker (creating the folder yielded a zero-byte file) was a wrong
        // MIME_TYPE_DIR in the SAF adapter; here we hold the name contract.
        var root = new StashPolicy(new StashPolicyOptions()).StashRoot.ToString();

        Assert.False(root.StartsWith('.'));
        Assert.Equal("FileFerry Trash", root);
    }

    [Fact]
    public void Add_add_identical_seeds_baseline_via_convergence()
    {
        // Provable shared state without a baseline row (variant A) ⟶ no-op RecordConvergence
        // operation — it "applies" through the journal and legally seeds the row.
        var plan = Plan(
            local: [File("a.txt", etag: "same")],
            remote: [File("a.txt", etag: "same")],
            baseline: []);

        var op = Assert.Single(plan.Operations);
        Assert.Equal(OperationKind.RecordConvergence, op.Kind);
        Assert.Equal("a.txt", op.Path.ToString());
        Assert.Null(op.SourcePath);
        Assert.NotNull(op.After); // the row must have something to seed: evidence from the sides
        Assert.Equal("same", op.After!.ETag);
        Assert.Empty(plan.Conflicts);
    }

    [Fact]
    public void Convergence_never_emits_for_rows_baseline_already_knows()
    {
        // Steady state: the row exists and both sides are equal — the plan stays empty (paired runs show "Plan: 0").
        var plan = Plan(
            local: [File("a.txt", etag: "e1")],
            remote: [File("a.txt", etag: "e1")],
            baseline: [File("a.txt", etag: "e1")]);

        Assert.Empty(plan.Operations);
        Assert.Empty(plan.Conflicts);
    }

    [Fact]
    public void Directory_add_add_seeds_directory_and_children()
    {
        var plan = Plan(
            local: [Dir("sub"), File("sub/x.txt", etag: "e")],
            remote: [Dir("sub"), File("sub/x.txt", etag: "e")],
            baseline: []);

        Assert.Collection(plan.Operations,
            op =>
            {
                Assert.Equal(OperationKind.RecordConvergence, op.Kind);
                Assert.Equal("sub", op.Path.ToString());
                Assert.True(op.After!.IsDirectory);
            },
            op =>
            {
                Assert.Equal(OperationKind.RecordConvergence, op.Kind);
                Assert.Equal("sub/x.txt", op.Path.ToString());
            });
    }

    [Fact]
    public void Add_add_without_any_evidence_still_conflicts()
    {
        // Bare size is not evidence: without hash/etag convergence is forbidden, KeepBoth as before.
        var bare = new FileSnapshot(RelativePath.Parse("p.txt"), EntryKind.File, 5, ModifiedAt: null);
        var result = Engine().Plan(
            Pair,
            Result<IReadOnlyList<FileSnapshot>, SyncError>.Success([bare]),
            Result<IReadOnlyList<FileSnapshot>, SyncError>.Success([bare]),
            [],
            Now);

        Assert.True(result.IsSuccess);
        Assert.Equal(3, result.Value.Operations.Count);
        Assert.DoesNotContain(result.Value.Operations, static o => o.Kind == OperationKind.RecordConvergence);
        Assert.Equal(ConflictKind.BothModified, Assert.Single(result.Value.Conflicts).Kind);
    }

    [Fact]
    public void Add_add_different_conflicts_keep_both()
    {
        var plan = Plan(
            local: [File("a.txt", etag: "L")],
            remote: [File("a.txt", etag: "R")],
            baseline: []);

        Assert.Equal(3, plan.Operations.Count);
        Assert.Equal(ConflictKind.BothModified, Assert.Single(plan.Conflicts).Kind);
        Assert.Contains(plan.Operations, static o => o.Kind == OperationKind.DownloadToLocal && o.Path.Name.Contains("conflicted copy"));
        Assert.Contains(plan.Operations, static o => o.Kind == OperationKind.UploadToRemote && o.Path.Name.Contains("conflicted copy"));
    }

    [Fact]
    public void Both_modified_conflicts_keep_both()
    {
        var plan = Plan(
            local: [File("a.txt", etag: "L")],
            remote: [File("a.txt", etag: "R")],
            baseline: [File("a.txt", etag: "base")]);

        Assert.Equal(3, plan.Operations.Count);
        var note = Assert.Single(plan.Conflicts);
        Assert.Equal(ConflictKind.BothModified, note.Kind);
        Assert.Equal("a.txt", note.Path.ToString());
        Assert.False(note.Folder); // files keep the keep-actions path
    }

    [Fact]
    public void Remote_deleted_local_modifies_keeps_local_edit()
    {
        var plan = Plan(
            local: [File("a.txt", etag: "L")],
            remote: [],
            baseline: [File("a.txt", etag: "base")]);

        var op = Assert.Single(plan.Operations);
        Assert.Equal(OperationKind.UploadToRemote, op.Kind);
        Assert.Equal(ConflictKind.DeleteVersusModify, Assert.Single(plan.Conflicts).Kind);
    }

    [Fact]
    public void Rename_local_single_becomes_move_on_remote()
    {
        var plan = Plan(
            local: [File("b.txt", etag: "e")],
            remote: [File("a.txt", etag: "e")],
            baseline: [File("a.txt", etag: "e")]);

        var op = Assert.Single(plan.Operations);
        Assert.Equal(OperationKind.MoveRemote, op.Kind);
        Assert.Equal("a.txt", op.SourcePath!.ToString());
        Assert.Equal("b.txt", op.Path.ToString());
    }

    /// <summary>
    /// The real device shape of a rename: the new path carries a device row with no evidence at
    /// all, and only the run's hashing pass over the rename shape can produce the shared proof —
    /// the planner must read through that hash instead of deleting the bytes and uploading them back.
    /// </summary>
    [Fact]
    public void Rename_OnDevice_WithHashedShape_PlansOneMove()
    {
        var plan = Plan(
            local: [new FileSnapshot(RelativePath.Parse("b.txt"), EntryKind.File, 8, ContentHash: "h1")],
            remote: [new FileSnapshot(RelativePath.Parse("a.txt"), EntryKind.File, 8, ETag: "e1", ContentHash: "h1")],
            baseline: [new FileSnapshot(RelativePath.Parse("a.txt"), EntryKind.File, 8, ETag: "e1", ContentHash: "h1")]);

        var op = Assert.Single(plan.Operations);
        Assert.Equal(OperationKind.MoveRemote, op.Kind);
        Assert.Equal("a.txt", op.SourcePath!.ToString());
        Assert.Equal("b.txt", op.Path.ToString());
        Assert.Empty(plan.Conflicts);
    }

    [Fact]
    public void Rename_chain_orders_moves_target_freeing_first()
    {
        // locally a→b and b→c; remote and baseline unchanged — both MOVEs to the server, b→c before a→b
        var plan = Plan(
            local: [File("b.txt", etag: "e"), File("c.txt", etag: "f")],
            remote: [File("a.txt", etag: "e"), File("b.txt", etag: "f")],
            baseline: [File("a.txt", etag: "e"), File("b.txt", etag: "f")]);

        Assert.Collection(plan.Operations,
            op =>
            {
                Assert.Equal(OperationKind.MoveRemote, op.Kind);
                Assert.Equal("b.txt", op.SourcePath!.ToString());
                Assert.Equal("c.txt", op.Path.ToString());
            },
            op =>
            {
                Assert.Equal(OperationKind.MoveRemote, op.Kind);
                Assert.Equal("a.txt", op.SourcePath!.ToString());
                Assert.Equal("b.txt", op.Path.ToString());
            });
    }

    [Fact]
    public void Case_collision_conflicts_keep_both()
    {
        var plan = Plan(
            local: [File("readme.txt", etag: "L")],
            remote: [File("README.txt", etag: "R")],
            baseline: []);

        Assert.Equal(3, plan.Operations.Count);
        var caseNote = Assert.Single(plan.Conflicts);
        Assert.Equal(ConflictKind.CaseCollision, caseNote.Kind);
        Assert.False(caseNote.Folder); // the copy was made — the keep actions can run
    }

    [Fact]
    public void DeleteVsModify_OnFolder_KeepsTheServerContainer_Silently()
    {
        // the folder itself vanished locally while the server's collection evidence moved: content
        // lives in the children (each keeps its own live version by the file rules); the container
        // is simply recreated — downloading a collection would be a dead step quarantining the pair
        var plan = Plan(
            local: [],
            remote: [Dir("photos", etag: "x")],
            baseline: [Dir("photos", etag: "b")]);

        Assert.Collection(plan.Operations,
            op => Assert.Equal(OperationKind.CreateLocalFolder, op.Kind));
        Assert.Empty(plan.Conflicts);
    }

    [Fact]
    public void DeleteVsModify_OnFolder_KeepsTheDeviceContainer_Silently()
    {
        // the mirror: the server lost the folder while the device tree moved under it
        var plan = Plan(
            local: [Dir("photos", etag: "x")],
            remote: [],
            baseline: [Dir("photos", etag: "b")]);

        Assert.Collection(plan.Operations,
            op => Assert.Equal(OperationKind.CreateRemoteFolder, op.Kind));
        Assert.Empty(plan.Conflicts);
    }

    [Fact]
    public void DeleteVsModify_OnFolder_WithMuteBaselineEvidence_ChildrenDecide()
    {
        // a folder row created by the engine itself carries no collection evidence until a scan
        // heals one. While the baseline row is mute, the children are the folder's evidence:
        // an entry the baseline never saw on the surviving side says that side moved — a stale
        // "unchanged" must never propagate the folder's deletion over content nobody saw yet
        var plan = Plan(
            local: [],
            remote: [Dir("photos"), File("photos/new.txt", etag: "n1")],
            baseline: [Dir("photos")]);

        Assert.Collection(plan.Operations,
            op => Assert.Equal(OperationKind.CreateLocalFolder, op.Kind),
            op => Assert.Equal(OperationKind.DownloadToLocal, op.Kind)); // the unseen server file comes down
        Assert.Empty(plan.Conflicts);
    }

    [Fact]
    public void DeleteVsModify_OnFolder_WithMuteBaselineEvidence_AndQuietChildren_StaysADeletion()
    {
        // the mirror of trust: nothing new on the surviving side, every child the baseline knows
        // — the deletion is honest and propagates (no phantom conflict, no resurrected folder);
        // the pre-deletion stash copy rides along, as every deletion must
        var plan = Plan(
            local: [],
            remote: [Dir("photos"), File("photos/a.txt", etag: "b1")],
            baseline: [Dir("photos"), File("photos/a.txt", etag: "b1")]);

        Assert.Contains(plan.Operations, static op =>
            op.Kind == OperationKind.DeleteRemote && op.Path.ToString() == "photos");
        Assert.Contains(plan.Operations, static op =>
            op.Kind == OperationKind.DeleteRemote && op.Path.ToString() == "photos/a.txt");
        Assert.All(plan.Operations, static op => Assert.True(
            op.Kind == OperationKind.DeleteRemote || op.Kind == OperationKind.DownloadToLocal));
        Assert.Empty(plan.Conflicts);
    }

    [Fact]
    public void CaseCollision_InvolvingAFolder_NotesWithoutDeadOperations()
    {
        // a folder clash cannot be safely duplicated: conflict copies are file-level, and on a
        // case-insensitive side a mkdir of the twin aliases onto the same folder. The engine plans
        // nothing dead and says so — one side has to be renamed by hand.
        var folderVsFolder = Plan(
            local: [Dir("Docs")],
            remote: [Dir("docs")],
            baseline: []);

        Assert.Empty(folderVsFolder.Operations);
        var folderNote = Assert.Single(folderVsFolder.Conflicts);
        Assert.Equal(ConflictKind.CaseCollision, folderNote.Kind);
        Assert.True(folderNote.Folder); // the row must read as hand-resolvable, not keep-actionable

        var folderVsFile = Plan(
            local: [File("Report.txt", etag: "e1")],
            remote: [Dir("report.txt")],
            baseline: []);

        Assert.Empty(folderVsFile.Operations);
        var clashNote = Assert.Single(folderVsFile.Conflicts);
        Assert.Equal(ConflictKind.CaseCollision, clashNote.Kind);
        Assert.True(clashNote.Folder);
    }

    [Fact]
    public void TypeClash_OnBothSide_NotesHandResolveWithoutOperations()
    {
        // a file locally and a folder on the server under one live path: copies are file-level
        // and a container cannot cross onto a foreign twin — zero operations, folder-marked note
        var plan = Plan(
            local: [File("clash", etag: "L")],
            remote: [Dir("clash", etag: "R")],
            baseline: [File("clash", etag: "base")]);

        Assert.Empty(plan.Operations);
        var note = Assert.Single(plan.Conflicts);
        Assert.Equal(ConflictKind.BothModified, note.Kind);
        Assert.True(note.Folder);
    }

    [Fact]
    public void TempWriteFragments_AreNotSyncState()
    {
        // a fragment of an interrupted local write is nobody's content: never uploaded, never
        // a conflict candidate; names that only look similar remain honest sync state
        var plan = Plan(
            local:
            [
                File("report.txt.tmp-0123456789abcdef0123456789abcdef", "junk"),
                File("keeps.tmp-hello", "k"),
            ],
            remote: [],
            baseline: []);

        Assert.Collection(plan.Operations,
            op =>
            {
                Assert.Equal(OperationKind.UploadToRemote, op.Kind);
                Assert.Equal("keeps.tmp-hello", op.Path.ToString());
            });
    }

    [Fact]
    public void Partial_scan_failure_halts_before_any_operation()
    {
        var planner = Engine();
        var remoteScan = Result<IReadOnlyList<FileSnapshot>, SyncError>.Failure(SyncError.Of(ErrorKind.ServerGone, "server burst"));

        var result = planner.Plan(Pair, Result<IReadOnlyList<FileSnapshot>, SyncError>.Success([]), remoteScan, baseline: [], Now);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorKind.ServerGone, result.Error.Kind);
    }

    [Fact]
    public void Folder_upload_orders_create_before_content()
    {
        var plan = Plan(
            local: [Dir("sub"), File("sub/x.txt", etag: "e")],
            remote: [],
            baseline: []);

        Assert.Collection(plan.Operations,
            op => Assert.Equal(OperationKind.CreateRemoteFolder, op.Kind),
            op => Assert.Equal(OperationKind.UploadToRemote, op.Kind));
    }

    // A run of 563 operations showed: SAF copying is file-only by contract,
    // yet the planner emitted StashCopy for directories too ⟶ "Conflict: directory;
    // only files are copied" halted the whole run. A directory's content is protected
    // by the children's own copy operations; an empty directory is not data loss.

    [Fact]
    public void Remote_deleted_directory_stashes_children_but_never_the_directory_itself()
    {
        var plan = Plan(
            local: [Dir("bulk"), File("bulk/a.txt", etag: "e1")],
            remote: [],
            baseline: [Dir("bulk"), File("bulk/a.txt", etag: "e1")]);

        Assert.Collection(plan.Operations,
            op =>
            {
                Assert.Equal(OperationKind.CopyLocal, op.Kind); // stash copy of the file child
                Assert.Equal("bulk/a.txt", op.SourcePath!.ToString());
            },
            op =>
            {
                Assert.Equal(OperationKind.DeleteLocal, op.Kind);
                Assert.Equal("bulk/a.txt", op.Path.ToString());
            },
            op =>
            {
                Assert.Equal(OperationKind.DeleteLocal, op.Kind); // directory — no copy, after the children
                Assert.Equal("bulk", op.Path.ToString());
            });
        Assert.DoesNotContain(plan.Operations, static op =>
            op.SourcePath is not null && op.SourcePath.ToString() == "bulk");
        Assert.Empty(plan.Conflicts);
    }

    [Fact]
    public void Local_deleted_directory_stashes_server_children_without_directory_copy()
    {
        var plan = Plan(
            local: [],
            remote: [Dir("bulk"), File("bulk/a.txt", etag: "e1")],
            baseline: [Dir("bulk"), File("bulk/a.txt", etag: "e1")]);

        Assert.Collection(plan.Operations,
            op =>
            {
                Assert.Equal(OperationKind.DownloadToLocal, op.Kind); // copy from the server into the stash
                Assert.Equal("bulk/a.txt", op.SourcePath!.ToString());
            },
            op =>
            {
                Assert.Equal(OperationKind.DeleteRemote, op.Kind);
                Assert.Equal("bulk/a.txt", op.Path.ToString());
            },
            op =>
            {
                Assert.Equal(OperationKind.DeleteRemote, op.Kind);
                Assert.Equal("bulk", op.Path.ToString());
            });
        Assert.DoesNotContain(plan.Operations, static op =>
            op.SourcePath is not null && op.SourcePath.ToString() == "bulk");
        Assert.Empty(plan.Conflicts);
    }

    [Fact]
    public void Empty_directory_deletion_needs_no_stash_copy()
    {
        var plan = Plan(
            local: [],
            remote: [Dir("bulk")],
            baseline: [Dir("bulk")]);

        var op = Assert.Single(plan.Operations);
        Assert.Equal(OperationKind.DeleteRemote, op.Kind);
        Assert.Null(op.SourcePath);
        Assert.Empty(plan.Conflicts);
    }

    /// <summary>
    /// Runs the three sets through the real pipeline (Diff → Plan) down to the operation list.
    /// </summary>
    private static PlanningResult Plan(
        IReadOnlyList<FileSnapshot> local,
        IReadOnlyList<FileSnapshot> remote,
        IReadOnlyList<FileSnapshot> baseline)
    {
        var result = Engine().Plan(
            Pair,
            Result<IReadOnlyList<FileSnapshot>, SyncError>.Success(local),
            Result<IReadOnlyList<FileSnapshot>, SyncError>.Success(remote),
            baseline,
            Now);

        Assert.True(result.IsSuccess, "the pipeline must not derail");
        return result.Value;
    }

    /// <summary>
    /// Pipeline with the default strategies (the same ones DI will compose).
    /// </summary>
    private static SyncPlanner Engine()
    {
        var stash = new StashPolicy(new StashPolicyOptions());
        return new SyncPlanner(new MergePlanner(new KeepBothStrategy(), stash), stash);
    }

    /// <summary>
    /// File snapshot carrying evidence (the ETag drives matching, not mtime).
    /// </summary>
    private static FileSnapshot File(string path, string etag) =>
        new(RelativePath.Parse(path), EntryKind.File, etag.Length, ModifiedAt: null, ETag: etag);

    private static FileSnapshot Dir(string path) =>
        new(RelativePath.Parse(path), EntryKind.Directory, 0);

    /// <summary>
    /// A directory whose collection evidence moved (the server stamps one; a child's change moves it).
    /// </summary>
    private static FileSnapshot Dir(string path, string etag) =>
        new(RelativePath.Parse(path), EntryKind.Directory, 0, ETag: etag);
}
