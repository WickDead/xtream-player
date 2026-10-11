namespace XtreamPlayer.Core.Models;

/// <summary>One side of a scheduled game: a team, or a fighter.</summary>
/// <param name="Names">
/// Every name it goes by, longest first: "New York Rangers", "Rangers", "NYR".
/// </param>
public sealed record GameSide(IReadOnlyList<string> Names);

/// <summary>A game or bout from an official schedule, with its real start time.</summary>
/// <param name="League">Where it came from, such as "nhl" or "ufc".</param>
/// <param name="Name">"Carolina Hurricanes at Montreal Canadiens".</param>
/// <param name="Start">The scheduled start, as an instant.</param>
/// <param name="Sides">The two teams or fighters.</param>
/// <param name="Stop">When the broadcast ends, for sources that publish it.</param>
public sealed record ScheduledGame(
    string League, string Name, DateTimeOffset Start, IReadOnlyList<GameSide> Sides, DateTimeOffset? Stop = null);
