using JNotes.Core;
using JNotes.Web.Auth;

namespace JNotes.Tests;

public class NoteTreeIndexTests
{
    private static readonly Guid Books = Guid.NewGuid(), SciFi = Guid.NewGuid(), Work = Guid.NewGuid(), Dune = Guid.NewGuid();

    private static NoteTreeIndex Sample() => new([
        new TreeEntry(Books, "Books", null, true),
        new TreeEntry(SciFi, "Sci-fi", Books, true),
        new TreeEntry(Work, "Work", null, true),
        new TreeEntry(Dune, "Dune", SciFi, false),
    ]);

    [Fact]
    public void Paths_and_ancestors_follow_the_folder_chain()
    {
        var index = Sample();
        Assert.Equal("Books / Sci-fi", index.PathOf(SciFi));
        Assert.Equal([Books, SciFi], index.AncestorsOf(Dune).Select(a => a.Id));
    }

    [Fact]
    public void A_folder_cannot_be_moved_into_itself_or_descendants()
    {
        var index = Sample();
        var destinations = index.MoveDestinationsFor(index.Find(Books)!).Select(d => d.Id);
        Assert.Equal([Work], destinations);
    }

    [Fact]
    public void A_note_can_move_to_any_folder()
    {
        var index = Sample();
        Assert.Equal(3, index.MoveDestinationsFor(index.Find(Dune)!).Count);
    }

    [Fact]
    public void Password_hash_round_trips()
    {
        var verifier = new PasswordVerifier(PasswordVerifier.Hash("correct horse"));
        Assert.True(verifier.Verify("correct horse"));
        Assert.False(verifier.Verify("wrong"));
        Assert.False(verifier.Verify(null));
    }
}
