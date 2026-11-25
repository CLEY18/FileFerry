using FileFerry.Core.Domain;
using FileFerry.Core.Domain.Paths;
using FileFerry.Core.Fakes;
using FileFerry.Core.Sync;

namespace FileFerry.Core.Tests.Sync;

/// <summary>
/// Folder-conflict resolutions built from the three-way state alone: four user actions —
/// rename or delete the losing folder on either side. A rename is one MOVE under a conflict
/// name (zero copies, both subtrees survive and sync on the next run); a delete puts every
/// file of the losing subtree into the stash buffer first — no disappearance without a copy.
/// </summary>
[Trait("Category", "Unit")]
public sealed class ConflictFolderResolverTests
{
    private const string Stamp = "20260101T000000"; // the MutableClock's instant, UTC-formatted
    private const string ConflictSuffix = $" (conflict {Stamp})";

    private readonly MutableClock _clock = new();
    private readonly StashPolicy _stash = new(new StashPolicyOptions());

    [Fact]
    public void RenameServer_CaseCollision_MovesTheServerTwinOneStep()
    {
        var plan = ConflictResolver.BuildFolder(
            Note("Docs"), FolderAction.RenameServer,
            Local(Dir("Docs")), Remote(Dir("docs"), File("docs/b.txt")), _stash, _clock.UtcNow);

        Assert.True(plan.IsSuccess);
        var op = Assert.Single(plan.Value.Operations);
        Assert.Equal(OperationKind.MoveRemote, op.Kind);
        Assert.Equal("docs", op.SourcePath!.ToString());
        Assert.Equal($"docs{ConflictSuffix}", op.Path.ToString());
    }

    [Fact]
    public void RenameLocal_CaseCollision_MovesTheDeviceFolderOneStep()
    {
        var plan = ConflictResolver.BuildFolder(
            Note("Docs"), FolderAction.RenameLocal,
            Local(Dir("Docs"), File("Docs/a.txt")), Remote(Dir("docs")), _stash, _clock.UtcNow);

        Assert.True(plan.IsSuccess);
        var op = Assert.Single(plan.Value.Operations);
        Assert.Equal(OperationKind.MoveLocal, op.Kind);
        Assert.Equal("Docs", op.SourcePath!.ToString());
        Assert.Equal($"Docs{ConflictSuffix}", op.Path.ToString());
    }

    [Fact]
    public void Rename_CollectionEntryTakesTheSuffixWhole_AndTheFileEntryAroundTheExtension()
    {
        var folder = ConflictResolver.BuildFolder(
            Note("Report.txt"), FolderAction.RenameServer,
            Local(File("Report.txt")), Remote(Dir("report.txt")), _stash, _clock.UtcNow);
        Assert.Equal($"report.txt{ConflictSuffix}", Assert.Single(folder.Value.Operations).Path.ToString());

        var file = ConflictResolver.BuildFolder(
            Note("clash"), FolderAction.RenameLocal,
            Local(File("clash")), Remote(Dir("clash")), _stash, _clock.UtcNow);
        Assert.Equal($"clash{ConflictSuffix}", Assert.Single(file.Value.Operations).Path.ToString());
    }

    [Fact]
    public void Rename_ChosenSideVanished_NotFound()
    {
        var plan = ConflictResolver.BuildFolder(
            Note("Docs"), FolderAction.RenameServer,
            Local(Dir("Docs")), Remote(), _stash, _clock.UtcNow);

        Assert.True(plan.IsFailure);
        Assert.Equal(ErrorKind.NotFound, plan.Error.Kind);
    }

    [Fact]
    public void DeleteServer_CaseCollision_StashesEveryServerFile_ThenDeletesDeepestFirst()
    {
        var plan = ConflictResolver.BuildFolder(
            Note("Docs"), FolderAction.DeleteServer,
            Local(Dir("Docs"), File("Docs/a.txt")),
            Remote(Dir("docs"), File("docs/b.txt"), Dir("docs/sub"), File("docs/sub/x.txt")),
            _stash, _clock.UtcNow);

        Assert.True(plan.IsSuccess);
        var ops = plan.Value.Operations;

        // the buffer first: both server files arrive under the stamped stash path
        Assert.Equal(OperationKind.DownloadToLocal, ops[0].Kind);
        Assert.Equal($"FileFerry Trash/{Stamp}/docs/b.txt", ops[0].Path.ToString());
        Assert.Equal(OperationKind.DownloadToLocal, ops[1].Kind);
        Assert.Equal($"FileFerry Trash/{Stamp}/docs/sub/x.txt", ops[1].Path.ToString());

        // then every deletion, deepest path first — the folders go last, emptied of their children
        var deletions = ops.Skip(2).Select(static op => (op.Kind, op.Path.ToString())).ToArray();
        Assert.Equal(
        [
            (OperationKind.DeleteRemote, "docs/sub/x.txt"),
            (OperationKind.DeleteRemote, "docs/b.txt"),
            (OperationKind.DeleteRemote, "docs/sub"),
            (OperationKind.DeleteRemote, "docs"),
        ],
            deletions);
    }

    [Fact]
    public void DeleteLocal_CaseCollision_CopiesFilesIntoTheBufferBeforeAnyDeletion()
    {
        var plan = ConflictResolver.BuildFolder(
            Note("Docs"), FolderAction.DeleteLocal,
            Local(Dir("Docs"), File("Docs/a.txt"), Dir("Docs/deep"), File("Docs/deep/z.txt")),
            Remote(Dir("docs")), _stash, _clock.UtcNow);

        Assert.True(plan.IsSuccess);
        var ops = plan.Value.Operations;

        Assert.Equal(OperationKind.CopyLocal, ops[0].Kind);
        Assert.Equal($"FileFerry Trash/{Stamp}/Docs/a.txt", ops[0].Path.ToString());
        Assert.Equal(OperationKind.CopyLocal, ops[1].Kind);
        Assert.Equal($"FileFerry Trash/{Stamp}/Docs/deep/z.txt", ops[1].Path.ToString());
        Assert.All(ops.Skip(2), static op => Assert.Equal(OperationKind.DeleteLocal, op.Kind));
        Assert.Equal("Docs", ops[^1].Path.ToString());
        Assert.Equal(EntryKind.Directory, ops[^1].After!.Kind);
    }

    [Fact]
    public void DeleteLocal_TypeClash_FileSideGoesThroughTheBuffer()
    {
        // the type clash's local side is a plain file: one copy, one deletion
        var plan = ConflictResolver.BuildFolder(
            Note("clash"), FolderAction.DeleteLocal,
            Local(File("clash")), Remote(Dir("clash")), _stash, _clock.UtcNow);

        Assert.True(plan.IsSuccess);
        Assert.Collection(plan.Value.Operations,
            op =>
            {
                Assert.Equal(OperationKind.CopyLocal, op.Kind);
                Assert.Equal($"FileFerry Trash/{Stamp}/clash", op.Path.ToString());
            },
            op =>
            {
                Assert.Equal(OperationKind.DeleteLocal, op.Kind);
                Assert.Equal("clash", op.Path.ToString());
            });
    }

    [Fact]
    public void DeleteServer_EmptyFolder_NoStashSteps()
    {
        var plan = ConflictResolver.BuildFolder(
            Note("Docs"), FolderAction.DeleteServer,
            Local(Dir("Docs")), Remote(Dir("docs")), _stash, _clock.UtcNow);

        var op = Assert.Single(plan.Value.Operations);
        Assert.Equal(OperationKind.DeleteRemote, op.Kind);
        Assert.Equal("docs", op.Path.ToString());
    }

    [Fact]
    public void Delete_ChosenSideVanished_NotFound()
    {
        var plan = ConflictResolver.BuildFolder(
            Note("Docs"), FolderAction.DeleteLocal,
            Local(), Remote(Dir("docs")), _stash, _clock.UtcNow);

        Assert.True(plan.IsFailure);
        Assert.Equal(ErrorKind.NotFound, plan.Error.Kind);
    }

    private static RelativePath Note(string path) => RelativePath.Parse(path);

    private static IReadOnlyDictionary<RelativePath, FileSnapshot> Local(params FileSnapshot[] rows) =>
        rows.ToDictionary(static r => r.Path, static r => r);

    private static IReadOnlyDictionary<RelativePath, FileSnapshot> Remote(params FileSnapshot[] rows) =>
        rows.ToDictionary(static r => r.Path, static r => r);

    private static FileSnapshot Dir(string path) => new(RelativePath.Parse(path), EntryKind.Directory, 0);

    private static FileSnapshot File(string path) =>
        new(RelativePath.Parse(path), EntryKind.File, 3, ETag: "\"e\"");
}
