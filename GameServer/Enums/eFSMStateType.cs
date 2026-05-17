namespace DOL.GS
{
    public enum eFSMStateType
    {
        WAKING_UP,
        IDLE,
        AGGRO,
        ROAMING,
        RETURN_TO_SPAWN,
        PATROLLING,
        PASSIVE,
        // MimicNPC module extensions
        CAMP,
        FOLLOW_THE_LEADER,
        DUEL,
        DEAD,
        CITY_IDLE
    }
}
