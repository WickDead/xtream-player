namespace XtreamPlayer.Core.Models;

/// <summary>
/// A non-empty profile id, required by every profile-scoped query.
/// </summary>
/// <remarks>
/// This exists to make one specific bug unrepresentable. The previous app's
/// <c>list_favorites</c> and <c>list_resume_history</c> selected with no
/// <c>WHERE profile_id = ?</c> at all, so every provider's favorites and resume
/// rows were merged into one list. It mostly hid because content ids rarely
/// collide across providers, which is exactly what made it hard to notice.
///
/// Taking a <see cref="ProfileScope"/> rather than a bare string means a caller
/// cannot forget to pass one, and cannot pass an empty one.
/// </remarks>
public readonly record struct ProfileScope
{
    public ProfileScope(string profileId)
    {
        if (string.IsNullOrWhiteSpace(profileId))
        {
            throw new ArgumentException(
                "A profile id is required. Profile-scoped queries must never run unscoped.",
                nameof(profileId));
        }

        Value = profileId;
    }

    public string Value { get; }

    public static implicit operator string(ProfileScope scope) => scope.Value;

    public static ProfileScope From(Profile profile) => new(profile.Id);

    public override string ToString() => Value;
}
