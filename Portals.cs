namespace OttoBifrost;

internal static class Portals
{
    private static readonly int WoodPortal = "portal_wood".GetStableHashCode();
    private static readonly int StonePortal = "portal_stone".GetStableHashCode();

    internal static bool IsPortal(ZDO zdo)
    {
        int prefab = zdo.GetPrefab();
        return prefab == WoodPortal || prefab == StonePortal;
    }

    internal static bool IsStonePortal(ZDO zdo)
    {
        return zdo.GetPrefab() == StonePortal;
    }

    internal static ZDO? ZdoOf(TeleportWorld portal)
    {
        return portal.m_nview != null && portal.m_nview.IsValid() ? portal.m_nview.GetZDO() : null;
    }

    /// Null when the portal has no connection, or when this game does not know the far portal.
    internal static ZDO? FarEnd(ZDO portal)
    {
        ZDOID id = portal.GetConnectionZDOID(ZDOExtraData.ConnectionType.Portal);
        return id == ZDOID.None || ZDOMan.instance == null ? null : ZDOMan.instance.GetZDO(id);
    }
}
