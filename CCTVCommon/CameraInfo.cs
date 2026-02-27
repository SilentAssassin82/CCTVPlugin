namespace CCTVCommon
{
    public class CameraInfo
    {
        public long EntityId;
        public string DisplayName;
        public int GridIndex;
        public int OwnerIndex;
        public string GridName;
        public long OwnerId;
        public long GridEntityId; // Grid the camera is on (for same-grid matching)
        public string FactionTag; // Faction tag of grid owner (for faction-based routing)
    }
}
