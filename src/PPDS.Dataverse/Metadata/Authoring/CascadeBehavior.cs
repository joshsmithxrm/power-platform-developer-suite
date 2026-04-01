namespace PPDS.Dataverse.Metadata.Authoring;

/// <summary>
/// Defines the cascade behavior options for entity relationship actions.
/// </summary>
public enum CascadeBehavior
{
    /// <summary>Cascade the action to all related records.</summary>
    Cascade,

    /// <summary>Cascade the action to all active related records.</summary>
    Active,

    /// <summary>Do not cascade the action.</summary>
    NoCascade,

    /// <summary>Cascade the action to all records owned by the same user.</summary>
    UserOwned,

    /// <summary>Remove the link to related records.</summary>
    RemoveLink,

    /// <summary>Restrict the action if related records exist.</summary>
    Restrict,
}
