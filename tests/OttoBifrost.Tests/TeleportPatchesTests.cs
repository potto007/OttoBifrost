using OttoBifrost.Patches;

namespace OttoBifrost.Tests;

public class TeleportPatchesTests
{
    // xunit theory rows must be public, and TripStep is internal to the mod.
    private const int Wait = (int)TeleportPatches.TripStep.Wait;
    private const int Finish = (int)TeleportPatches.TripStep.Finish;
    private const int HandOver = (int)TeleportPatches.TripStep.HandOver;

    [Theory]
    // The server said the destination is complete: nothing else matters.
    [InlineData(true, 0f, 0f, true)]
    // Otherwise the server needs 1 s to react to the move and the count must hold 0.5 s.
    [InlineData(false, 1f, 0.5f, true)]
    [InlineData(false, 0.9f, 5f, false)]
    [InlineData(false, 5f, 0.4f, false)]
    public void Arrival_data_settles_on_server_word_or_a_still_object_count(bool serverComplete, float sinceMove, float sinceChange, bool expected)
    {
        Assert.Equal(expected, TeleportPatches.ArrivalSettled(serverComplete, sinceMove, sinceChange));
    }

    [Theory]
    // Everything ready: land.
    [InlineData(true, true, true, 2.5f, Finish)]
    // No floor yet: wait until the floor search times out at 3 s, then land anyway.
    [InlineData(true, true, false, 2.5f, Wait)]
    [InlineData(true, true, false, 3.1f, Finish)]
    // Objects or data still missing: wait.
    [InlineData(false, true, true, 5f, Wait)]
    [InlineData(true, false, true, 5f, Wait)]
    // Past the vanilla 8 s mark, vanilla takes over whatever is missing.
    [InlineData(false, true, true, 8.1f, HandOver)]
    [InlineData(true, false, false, 8.1f, HandOver)]
    // Ready at the 8 s mark still lands, so a fast trip is never slower than vanilla.
    [InlineData(true, true, true, 8.1f, Finish)]
    public void A_fast_trip_lands_waits_or_hands_over(bool nearBuilt, bool dataSettled, bool floorFound, float timer, int expected)
    {
        Assert.Equal((TeleportPatches.TripStep)expected, TeleportPatches.Decide(nearBuilt, dataSettled, floorFound, timer));
    }
}
