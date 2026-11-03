using QuickMatch.Matchmaking;
using Xunit;

namespace QuickMatch.Tests;

public class MatchPolicyTests
{
    private static readonly Func<ulong, bool> SkipNone = _ => false;

    [Fact]
    public void Rank_PrefersFullerThenOlderThenLowerId()
    {
        var newPair = new LobbyCandidate(Id: 10, Members: 2, CreatedAt: 500);
        var oldSolo = new LobbyCandidate(Id: 20, Members: 1, CreatedAt: 100);
        var sameAgeSoloHigherId = new LobbyCandidate(Id: 30, Members: 1, CreatedAt: 100);
        var newSolo = new LobbyCandidate(Id: 5, Members: 1, CreatedAt: 900);

        List<LobbyCandidate> ranked = MatchPolicy.Rank([newSolo, sameAgeSoloHigherId, oldSolo, newPair], ownLobbyId: 0, SkipNone);

        Assert.Equal([newPair, oldSolo, sameAgeSoloHigherId, newSolo], ranked);
    }

    [Fact]
    public void Rank_DropsOwnLobbySkippedIncompleteAndDuplicates()
    {
        var own = new LobbyCandidate(1, 1, 100);
        var failed = new LobbyCandidate(2, 1, 100);
        var noData = new LobbyCandidate(3, 1, 0);
        var good = new LobbyCandidate(4, 1, 100);

        List<LobbyCandidate> ranked = MatchPolicy.Rank([own, failed, noData, good, good], ownLobbyId: 1, id => id == 2);

        Assert.Equal([good], ranked);
    }

    [Fact]
    public void LoneHostYieldsToFullerLobbyEvenIfNewer()
    {
        var own = new LobbyCandidate(1, 1, 100);
        var pair = new LobbyCandidate(2, 2, 999);

        Assert.Equal(pair, MatchPolicy.PickMergeTarget(own, [pair], SkipNone, size: 4));
    }

    [Fact]
    public void LoneHostKeepsWaitingWhenOnlyNewerLoneHostsExist()
    {
        var own = new LobbyCandidate(1, 1, 100);
        var newer = new LobbyCandidate(2, 1, 200);

        Assert.Null(MatchPolicy.PickMergeTarget(own, [newer, own], SkipNone, size: 4));
    }

    [Fact]
    public void OfTwoLoneHostsExactlyOneYields()
    {
        var rng = new Random(1234);
        for (int i = 0; i < 2000; i++)
        {
            // Small ranges so ties on creation time (and the id tiebreak) come up often.
            var a = new LobbyCandidate((ulong)rng.Next(1, 50), 1, (uint)rng.Next(1, 5));
            var b = new LobbyCandidate((ulong)rng.Next(1, 50), 1, (uint)rng.Next(1, 5));
            if (a.Id == b.Id)
            {
                continue;
            }
            bool aYields = MatchPolicy.PickMergeTarget(a, [a, b], SkipNone, size: 2) == b;
            bool bYields = MatchPolicy.PickMergeTarget(b, [a, b], SkipNone, size: 2) == a;
            Assert.True(aYields ^ bYields, $"a={a} b={b} aYields={aYields} bYields={bYields}");
        }
    }

    [Fact]
    public void TwoPairsForFourMergeIntoTheOlderLobby()
    {
        var older = new LobbyCandidate(1, 2, 100);
        var newer = new LobbyCandidate(2, 2, 200);

        Assert.Equal(older, MatchPolicy.PickMergeTarget(newer, [older, newer], SkipNone, size: 4));
        Assert.Null(MatchPolicy.PickMergeTarget(older, [older, newer], SkipNone, size: 4));
    }

    [Fact]
    public void TwoPairsForThreeDoNotMerge()
    {
        var older = new LobbyCandidate(1, 2, 100);
        var newer = new LobbyCandidate(2, 2, 200);

        Assert.Null(MatchPolicy.PickMergeTarget(newer, [older, newer], SkipNone, size: 3));
        Assert.Null(MatchPolicy.PickMergeTarget(older, [older, newer], SkipNone, size: 3));
    }

    [Fact]
    public void LoneHostMovesIntoTrioForFour()
    {
        var trio = new LobbyCandidate(1, 3, 900);
        var solo = new LobbyCandidate(2, 1, 100);

        Assert.Equal(trio, MatchPolicy.PickMergeTarget(solo, [trio, solo], SkipNone, size: 4));
        Assert.Null(MatchPolicy.PickMergeTarget(trio, [trio, solo], SkipNone, size: 4));
    }

    [Fact]
    public void GroupSkipsBetterLobbyWithoutRoomForItButTakesTheNextOne()
    {
        var trio = new LobbyCandidate(1, 3, 100);
        var olderPair = new LobbyCandidate(2, 2, 100);
        var ownPair = new LobbyCandidate(3, 2, 200);

        Assert.Equal(olderPair, MatchPolicy.PickMergeTarget(ownPair, [trio, olderPair, ownPair], SkipNone, size: 4));
    }

    [Fact]
    public void FullLobbyNeverMoves()
    {
        var full = new LobbyCandidate(5, 4, 900);
        var other = new LobbyCandidate(1, 1, 1);

        Assert.Null(MatchPolicy.PickMergeTarget(full, [other], SkipNone, size: 4));
    }

    [Fact]
    public void OfTwoPartialGroupsAtMostOneMovesAndNeverIntoEachOther()
    {
        var rng = new Random(99);
        for (int i = 0; i < 2000; i++)
        {
            int size = rng.Next(2, 5);
            var a = new LobbyCandidate((ulong)rng.Next(1, 50), rng.Next(1, size), (uint)rng.Next(1, 5));
            var b = new LobbyCandidate((ulong)rng.Next(1, 50), rng.Next(1, size), (uint)rng.Next(1, 5));
            if (a.Id == b.Id)
            {
                continue;
            }
            bool aMoves = MatchPolicy.PickMergeTarget(a, [a, b], SkipNone, size) == b;
            bool bMoves = MatchPolicy.PickMergeTarget(b, [a, b], SkipNone, size) == a;
            Assert.False(aMoves && bMoves, $"size={size} a={a} b={b}");
            if (a.Members + b.Members <= size)
            {
                Assert.True(aMoves ^ bMoves, $"size={size} a={a} b={b} should merge");
            }
        }
    }

    [Fact]
    public void RunKey_IsOrderIndependentAndSeedSensitive()
    {
        string key = MatchPolicy.RunKey("ABC123", [76561198000000002, 76561198000000001]);

        Assert.Equal(key, MatchPolicy.RunKey("ABC123", [76561198000000001, 76561198000000002]));
        Assert.NotEqual(key, MatchPolicy.RunKey("ABC124", [76561198000000001, 76561198000000002]));
        Assert.NotEqual(key, MatchPolicy.RunKey("ABC123", [76561198000000001, 76561198000000003]));
        Assert.Matches("^[0-9a-f]{24}$", key);
    }

    [Fact]
    public void ModsHash_NoModsIsNone()
    {
        Assert.Equal("none", MatchPolicy.ModsHash(null));
        Assert.Equal("none", MatchPolicy.ModsHash([]));
        Assert.Equal("none", MatchPolicy.ModsHash([""]));
    }

    [Fact]
    public void ModsHash_IsOrderIndependentAndStable()
    {
        string a = MatchPolicy.ModsHash(["BaseLib-v3.4.7", "Act4Heart-v1.0.0"]);
        string b = MatchPolicy.ModsHash(["Act4Heart-v1.0.0", "BaseLib-v3.4.7", "BaseLib-v3.4.7"]);

        Assert.Equal(a, b);
        Assert.Equal(16, a.Length);
        Assert.Matches("^[0-9a-f]{16}$", a);
        // Fixed value: the hash is sent to other players, so it must not change between builds.
        Assert.Equal("7c995d9a1f91f675", a);
    }

    [Fact]
    public void ModsHash_DiffersWhenVersionsDiffer()
    {
        Assert.NotEqual(
            MatchPolicy.ModsHash(["BaseLib-v3.4.7"]),
            MatchPolicy.ModsHash(["BaseLib-v3.4.8"]));
    }

    [Fact]
    public void AMoveIsFollowedOnlyFromTheHost()
    {
        const ulong host = 76561198000000301, other = 76561198000000302, lobby = 109775242500000001, target = 109775242500000002;

        Assert.True(MatchPolicy.AcceptsMove(host, host, target, lobby));
        // Another player in the lobby cannot send the group elsewhere.
        Assert.False(MatchPolicy.AcceptsMove(other, host, target, lobby));
        // Nor can anyone when the lobby's owner is unknown, and a move needs a lobby to go to.
        Assert.False(MatchPolicy.AcceptsMove(other, 0, target, lobby));
        Assert.False(MatchPolicy.AcceptsMove(host, host, 0, lobby));
        Assert.False(MatchPolicy.AcceptsMove(host, host, lobby, lobby));
    }
}
