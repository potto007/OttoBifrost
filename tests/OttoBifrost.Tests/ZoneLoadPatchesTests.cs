using OttoBifrost.Patches;
using UnityEngine;

namespace OttoBifrost.Tests;

public class ZoneLoadPatchesTests
{
    // The game's zones are 64 m squares centred on multiples of 64.
    private const float ZoneSize = 64f;

    [Theory]
    // The zone the point stands in.
    [InlineData(0f, 0f, 0, 0, true)]
    // A neighbour 32 m away along one axis is within 40 m; two zones over is not.
    [InlineData(0f, 0f, 1, 0, true)]
    [InlineData(0f, 0f, 2, 0, false)]
    // The diagonal neighbour's corner is 45 m from the zone centre.
    [InlineData(0f, 0f, 1, 1, false)]
    // Standing near a corner brings the diagonal neighbour within reach.
    [InlineData(30f, 30f, 1, 1, true)]
    [InlineData(-30f, 30f, -1, 1, true)]
    public void A_zone_is_near_when_any_part_of_it_is_within_forty_meters(float x, float z, int zoneX, int zoneY, bool expected)
    {
        Assert.Equal(expected, ZoneLoadPatches.WithinPrimeRadius(ZoneSize, new Vector2s(zoneX, zoneY), new Vector3(x, 100f, z)));
    }

    [Theory]
    [InlineData(ZDO.ObjectType.Default, 39f, true)]
    [InlineData(ZDO.ObjectType.Default, 41f, false)]
    [InlineData(ZDO.ObjectType.Solid, 40f, true)]
    // A terrain edit shapes its whole zone, so it always counts.
    [InlineData(ZDO.ObjectType.Terrain, 500f, true)]
    public void An_object_is_within_radius_by_straight_line_distance(ZDO.ObjectType type, float distance, bool expected)
    {
        Assert.Equal(expected, ZoneLoadPatches.WithinRadius(type, distance * distance, 40f));
    }

    [Fact]
    public void Height_counts_toward_the_radius()
    {
        Vector3 offset = new(10f, 3000f, 0f);
        Assert.False(ZoneLoadPatches.WithinRadius(ZDO.ObjectType.Solid, offset.sqrMagnitude, 40f));
    }

    [Theory]
    [InlineData(0f, 0f, -3f, true)]
    [InlineData(0f, 0f, -1.5f, false)]
    [InlineData(0f, 0f, 3f, false)]
    // Height does not count: the object is judged on the ground plane.
    [InlineData(0f, 500f, -3f, true)]
    public void An_object_is_behind_when_more_than_the_allowance_behind_the_portal(float x, float y, float z, bool expected)
    {
        Assert.Equal(expected, ZoneLoadPatches.IsBehind(ZDO.ObjectType.Solid, new Vector3(x, y, z), Vector3.forward, 2f));
    }

    [Fact]
    public void A_terrain_edit_is_never_behind()
    {
        Assert.False(ZoneLoadPatches.IsBehind(ZDO.ObjectType.Terrain, new Vector3(0f, 0f, -50f), Vector3.forward, 2f));
    }

    [Fact]
    public void An_empty_pass_doubles_the_wait_up_to_two_seconds_and_a_create_resets_it()
    {
        float wait = 0.2f;
        wait = ZoneLoadPatches.NextPrimeWait(wait, createdAny: false);
        Assert.Equal(0.4f, wait, 3);
        wait = ZoneLoadPatches.NextPrimeWait(wait, createdAny: false);
        Assert.Equal(0.8f, wait, 3);
        wait = ZoneLoadPatches.NextPrimeWait(wait, createdAny: false);
        Assert.Equal(1.6f, wait, 3);
        wait = ZoneLoadPatches.NextPrimeWait(wait, createdAny: false);
        Assert.Equal(2f, wait, 3);
        wait = ZoneLoadPatches.NextPrimeWait(wait, createdAny: false);
        Assert.Equal(2f, wait, 3);
        wait = ZoneLoadPatches.NextPrimeWait(wait, createdAny: true);
        Assert.Equal(0.2f, wait, 3);
    }

    [Fact]
    public void Creation_order_is_type_then_portals_then_in_front_then_nearest()
    {
        List<ZoneLoadPatches.Candidate> queue = new()
        {
            Candidate(ZDO.ObjectType.Default, portal: false, behind: false, distance: 5f),
            Candidate(ZDO.ObjectType.Solid, portal: false, behind: true, distance: 1f),
            Candidate(ZDO.ObjectType.Solid, portal: false, behind: false, distance: 20f),
            Candidate(ZDO.ObjectType.Terrain, portal: false, behind: false, distance: 30f),
            Candidate(ZDO.ObjectType.Solid, portal: false, behind: false, distance: 10f),
            Candidate(ZDO.ObjectType.Solid, portal: true, behind: false, distance: 15f),
            Candidate(ZDO.ObjectType.Prioritized, portal: false, behind: false, distance: 2f),
        };

        queue.Sort(ZoneLoadPatches.ByPriority);

        Assert.Collection(queue,
            c => Assert.Equal(ZDO.ObjectType.Terrain, c.Type),
            c => { Assert.Equal(ZDO.ObjectType.Solid, c.Type); Assert.True(c.Portal); },
            c => { Assert.Equal(ZDO.ObjectType.Solid, c.Type); Assert.Equal(100f, c.DistanceSqr); },
            c => { Assert.Equal(ZDO.ObjectType.Solid, c.Type); Assert.Equal(400f, c.DistanceSqr); },
            c => { Assert.Equal(ZDO.ObjectType.Solid, c.Type); Assert.True(c.Behind); },
            c => Assert.Equal(ZDO.ObjectType.Prioritized, c.Type),
            c => Assert.Equal(ZDO.ObjectType.Default, c.Type));
    }

    private static ZoneLoadPatches.Candidate Candidate(ZDO.ObjectType type, bool portal, bool behind, float distance)
    {
        return new ZoneLoadPatches.Candidate(null, type, portal, behind, distance * distance);
    }
}
