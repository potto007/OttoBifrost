using OttoBifrost;
using UnityEngine;

namespace OttoBifrost.Tests;

/// Destinations keeps static state, so each test starts from Clear.
public class DestinationsTests
{
    private static readonly ZDOID PortalA = new(1L, 1U);
    private static readonly ZDOID PortalB = new(1L, 2U);
    private static readonly ZDOID PortalC = new(1L, 3U);
    // Far ends in separate zones, 64 m apart.
    private static readonly Vector3 FarA = new(1000f, 30f, 1000f);
    private static readonly Vector3 FarB = new(2000f, 30f, 2000f);
    private static readonly Vector3 FarC = new(3000f, 30f, 3000f);

    public DestinationsTests()
    {
        Destinations.Clear();
    }

    [Theory]
    [InlineData(15f, 10f, 1)]
    [InlineData(10f, 10f, 2)]
    [InlineData(5f, 10f, 3)]
    [InlineData(2f, 10f, 3)]
    // The area grows one ring per 0.4 s from the first request.
    [InlineData(2f, 0f, 1)]
    [InlineData(2f, 0.39f, 1)]
    [InlineData(2f, 0.4f, 2)]
    [InlineData(2f, 0.8f, 3)]
    [InlineData(12f, 5f, 1)]
    public void Radius_follows_distance_and_grows_over_time(float distance, float seconds, int expected)
    {
        Assert.Equal(expected, Destinations.RadiusFor(distance, seconds));
    }

    [Fact]
    public void Nearest_loads_first_at_full_size_and_the_rest_at_preview_size_by_distance()
    {
        Destinations.Request(PortalC, FarC, Vector3.forward, 3, 12f);
        Destinations.Request(PortalA, FarA, Vector3.forward, 3, 4f);
        Destinations.Request(PortalB, FarB, Vector3.forward, 3, 8f);

        Assert.Collection(Destinations.Active,
            d => { Assert.Equal(FarA, d.Position); Assert.Equal(3, d.Radius); },
            d => { Assert.Equal(FarB, d.Position); Assert.Equal(1, d.Radius); },
            d => { Assert.Equal(FarC, d.Position); Assert.Equal(1, d.Radius); });
    }

    [Fact]
    public void Nearest_only_switches_once_another_portal_is_two_meters_closer()
    {
        Destinations.Request(PortalA, FarA, Vector3.forward, 3, 6f);
        Destinations.Request(PortalB, FarB, Vector3.forward, 3, 5f);
        Assert.Equal(FarA, Destinations.Active[0].Position);

        Destinations.Request(PortalB, FarB, Vector3.forward, 3, 4.1f);
        Assert.Equal(FarA, Destinations.Active[0].Position);

        Destinations.Request(PortalB, FarB, Vector3.forward, 3, 3.9f);
        Assert.Equal(FarB, Destinations.Active[0].Position);
        Assert.Equal(3, Destinations.Active[0].Radius);
        Assert.Equal(1, Destinations.Active[1].Radius);
    }

    [Fact]
    public void Release_removes_a_destination()
    {
        Destinations.Request(PortalA, FarA, Vector3.forward, 3, 4f);
        Destinations.Request(PortalB, FarB, Vector3.forward, 3, 8f);
        Destinations.Release(PortalA);

        Destination only = Assert.Single(Destinations.Active);
        Assert.Equal(FarB, only.Position);
        Assert.Equal(3, only.Radius);
    }

    [Fact]
    public void Release_during_a_teleport_waits_until_the_player_has_landed()
    {
        Destinations.Request(PortalA, FarA, Vector3.forward, 3, 2f);
        Destinations.BeginTeleport();
        Destinations.Release(PortalA);
        Assert.Equal(FarA, Assert.Single(Destinations.Active).Position);

        Destinations.EndTeleport(FarA + Vector3.forward, 5f, now: 100f);
        Destination landing = Assert.Single(Destinations.Active);
        Assert.Equal(FarA + Vector3.forward, landing.Position);
        Assert.Equal(Destinations.ArrivalRadius, landing.Radius);
    }

    [Fact]
    public void Landing_spot_is_held_for_the_given_time_then_dropped()
    {
        Destinations.BeginTeleport();
        Destinations.EndTeleport(FarA, 5f, now: 100f);
        Assert.Single(Destinations.Active);

        Destinations.Tick(104.9f);
        Assert.Single(Destinations.Active);

        Destinations.Tick(105f);
        Assert.Empty(Destinations.Active);
    }

    [Fact]
    public void Landing_spot_yields_to_the_nearest_portal_and_shrinks_to_preview_size()
    {
        Destinations.BeginTeleport();
        Destinations.EndTeleport(FarA, 5f, now: 100f);
        Destinations.Request(PortalB, FarB, Vector3.forward, 3, 3f);

        Assert.Collection(Destinations.Active,
            d => { Assert.Equal(FarB, d.Position); Assert.Equal(3, d.Radius); },
            d => { Assert.Equal(FarA, d.Position); Assert.Equal(Destinations.SecondaryRadius, d.Radius); });
    }

    [Fact]
    public void Covers_the_square_of_zones_around_each_destination()
    {
        Destinations.Request(PortalA, FarA, Vector3.forward, 2, 4f);
        Vector2s centre = ZoneSystem.GetZone(FarA);

        Assert.True(Destinations.Covers(centre));
        Assert.True(Destinations.Covers(new Vector2s(centre.x + 2, centre.y - 2)));
        Assert.False(Destinations.Covers(new Vector2s(centre.x + 3, centre.y)));
        Assert.False(Destinations.Covers(ZoneSystem.GetZone(FarB)));
    }

    [Fact]
    public void IsNear_matches_a_point_within_tolerance_of_a_destination()
    {
        Destinations.Request(PortalA, FarA, Vector3.forward, 1, 4f);

        Assert.True(Destinations.IsNear(FarA + new Vector3(3f, 1f, 0f), 10f));
        Assert.False(Destinations.IsNear(FarA + new Vector3(10f, 0f, 0f), 10f));
        Assert.False(Destinations.IsNear(FarB, 10f));
    }

    [Fact]
    public void Clear_forgets_everything()
    {
        Destinations.Request(PortalA, FarA, Vector3.forward, 3, 4f);
        Destinations.BeginTeleport();
        Destinations.Clear();

        Assert.False(Destinations.Any);
        Destinations.Release(PortalA);
        Assert.False(Destinations.Any);
    }
}
