using System;
using System.Collections.Generic;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.LocalPrerollsPro.Configuration;

/// <summary>
/// Plugin settings. Saved as XML by Jellyfin and edited as JSON by the settings page.
/// </summary>
/// <remarks>
/// List properties start empty on purpose: XmlSerializer appends to pre-filled lists,
/// which would duplicate defaults on every load. Defaults are filled in by
/// <see cref="ConfigMigrator"/> when <see cref="SchemaVersion"/> is below the current version.
/// Enum-like values are stored as strings so the XML and JSON forms stay readable and stable.
/// </remarks>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>Current settings layout. 0 means "never migrated" (fresh install or v1 config).</summary>
    public const int CurrentSchemaVersion = 2;

    public int SchemaVersion { get; set; }

    /// <summary>Folders (or single files) that hold intro videos.</summary>
    public List<string> IntroFolders { get; set; } = new();

    public bool ScanSubfolders { get; set; } = true;

    /// <summary>Comma separated list of file extensions treated as intro videos.</summary>
    public string VideoExtensions { get; set; } = DefaultVideoExtensions;

    public const string DefaultVideoExtensions = ".mp4,.m4v,.mkv,.mov,.webm,.avi,.ts,.m2ts,.wmv";

    public List<IntroRule> Rules { get; set; } = new();

    /// <summary>What plays before the feature, in order.</summary>
    public List<PreShowStep> PreShow { get; set; } = new();

    /// <summary>Feature types that get intros (Jellyfin item kinds such as Movie, Episode).</summary>
    public List<string> EnabledItemTypes { get; set; } = new();

    /// <summary>One of <see cref="SelectionModes"/>.</summary>
    public string SelectionMode { get; set; } = SelectionModes.WeightedRandom;

    /// <summary>Skip intros a user saw in their last N intros, when alternatives exist.</summary>
    public int AvoidRepeatCount { get; set; } = 3;

    /// <summary>Do not play intros for a user more often than this. 0 = no limit.</summary>
    public int MinMinutesBetweenIntros { get; set; }

    /// <summary>One of <see cref="EpisodeModes"/>.</summary>
    public string EpisodeMode { get; set; } = EpisodeModes.EveryEpisode;

    /// <summary>For <see cref="EpisodeModes.FirstOfBinge"/>: a gap this long starts a new binge.</summary>
    public int BingeGapMinutes { get; set; } = 120;

    /// <summary>Users (Jellyfin user ids) who never get intros.</summary>
    public List<string> ExcludedUserIds { get; set; } = new();

    // ---- Per-file settings ----

    /// <summary>Settings made on the settings page for individual intros. They override sidecar files.</summary>
    public List<IntroFileSettings> FileSettings { get; set; } = new();

    /// <summary>Never play an intro rated above the title (e.g. no R-rated intro before a PG film).</summary>
    public bool AgeSafe { get; set; } = true;

    /// <summary>For titles without a rating: true = any intro, false = only unrated intros.</summary>
    public bool AgeSafeUnratedAllowAll { get; set; }

    /// <summary>New intros play more often for this many days. 0 = off.</summary>
    public int BoostNewDays { get; set; } = 7;

    public double BoostNewFactor { get; set; } = 2;

    // ---- Per-title intros and opt-outs ----

    /// <summary>Play intros found in a folder with this name inside a movie, series or season folder.</summary>
    public bool TitleIntrosEnabled { get; set; } = true;

    public string TitleFolderName { get; set; } = ".prerolls";

    /// <summary>One of <see cref="TitleIntroModes"/>.</summary>
    public string TitleIntrosMode { get; set; } = TitleIntroModes.Replace;

    /// <summary>Movies, series, seasons or episodes (item ids) that never get intros.</summary>
    public List<string> OptOutItemIds { get; set; } = new();

    /// <summary>Titles with this tag never get intros. Empty = off.</summary>
    public string OptOutTag { get; set; } = "no-preroll";

    // ---- Conversion and loudness ----

    /// <summary>Make H.264/AAC copies of intros that clients can't play directly.</summary>
    public bool ConvertProblemFiles { get; set; } = true;

    /// <summary>Make volume-matched copies of intros far from the target loudness.</summary>
    public bool LevelLoudness { get; set; }

    /// <summary>Target loudness in LUFS for levelling and warnings.</summary>
    public double TargetLoudness { get; set; } = -24;

    // ---- prerolls.me catalog ----

    public bool CatalogEnabled { get; set; } = true;

    /// <summary>Folder for downloads. Empty = the first intro folder.</summary>
    public string CatalogDownloadFolder { get; set; } = string.Empty;

    /// <summary>Put downloads in a subfolder named after their category.</summary>
    public bool CatalogFolderPerCategory { get; set; } = true;

    /// <summary>Check subscriptions for new prerolls about once an hour.</summary>
    public bool CatalogAutoSync { get; set; } = true;

    public List<CatalogSubscription> CatalogSubscriptions { get; set; } = new();

    // ---- Settings from v1 (schema 0). Read once by ConfigMigrator, then cleared. ----

    public string Local { get; set; } = string.Empty;

    public List<IntroVideo> DetectedLocalVideos { get; set; } = new();

    public List<Guid> DefaultLocalVideos { get; set; } = new();

    public List<TagIntro> TagIntros { get; set; } = new();

    public List<GenreIntro> GenreIntros { get; set; } = new();

    public List<StudioIntro> StudioIntros { get; set; } = new();

    public List<DateRangeIntro> CurrentDateIntros { get; set; } = new();

    public List<DateRangeIntro> PremiereDateIntros { get; set; } = new();

    public bool IntrosForMoviesOnly { get; set; }
}

public static class TitleIntroModes
{
    /// <summary>Title intros are used instead of rule picks.</summary>
    public const string Replace = "Replace";

    /// <summary>Title intros play first, then the normal pre-show.</summary>
    public const string AddFirst = "AddFirst";
}

/// <summary>Settings for one intro made on the settings page. Null or empty fields are "not set".</summary>
public class IntroFileSettings
{
    public string Key { get; set; } = string.Empty;

    public string Name { get; set; }

    public List<string> Tags { get; set; } = new();

    public double? Weight { get; set; }

    public string Rating { get; set; }

    public string From { get; set; }

    public string To { get; set; }

    public bool? Enabled { get; set; }
}

/// <summary>Keeps a category, tag or platform from prerolls.me downloaded.</summary>
public class CatalogSubscription
{
    /// <summary>"category", "tag" or "platform".</summary>
    public string Kind { get; set; } = "category";

    public string Value { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;

    /// <summary>Only prerolls made for this platform ("jellyfin", "plex", "emby") or empty for any.</summary>
    public string Platform { get; set; } = string.Empty;
}

public static class SelectionModes
{
    /// <summary>Random pick, weighted by each rule's weight.</summary>
    public const string WeightedRandom = "WeightedRandom";

    /// <summary>Intro with the fewest plays wins (ties broken by weighted random).</summary>
    public const string LeastPlayed = "LeastPlayed";

    /// <summary>Round-robin through the matching intros in name order.</summary>
    public const string Sequential = "Sequential";
}

public static class EpisodeModes
{
    public const string EveryEpisode = "EveryEpisode";

    /// <summary>Only the first episode of a viewing session of a series.</summary>
    public const string FirstOfBinge = "FirstOfBinge";

    /// <summary>At most once per series per day.</summary>
    public const string OncePerSeriesPerDay = "OncePerSeriesPerDay";
}

public static class PreShowStepKinds
{
    /// <summary>Intros chosen by the rules.</summary>
    public const string Rules = "Rules";

    /// <summary>Always the listed intros, in order (e.g. a "feature presentation" bumper).</summary>
    public const string Fixed = "Fixed";

    /// <summary>Local trailers of other movies the user has not watched.</summary>
    public const string Trailers = "Trailers";
}

/// <summary>
/// A rule picks intros when its conditions match the feature being played.
/// </summary>
public class IntroRule
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;

    /// <summary>Optional group. A pre-show Rules step can be limited to one group.</summary>
    public string Group { get; set; } = string.Empty;

    /// <summary>Only matching rules with the highest precedence are used.</summary>
    public int Precedence { get; set; } = 1;

    /// <summary>Relative chance of each of this rule's intros being picked.</summary>
    public int Weight { get; set; } = 1;

    /// <summary>"All" (every condition must match) or "Any".</summary>
    public string MatchMode { get; set; } = "All";

    /// <summary>
    /// Intro keys (path relative to its intro folder, using "/"),
    /// a folder ending in "/" for every intro inside it, or "*" for all intros.
    /// </summary>
    public List<string> Intros { get; set; } = new();

    /// <summary>No conditions means the rule always matches.</summary>
    public List<RuleCondition> Conditions { get; set; } = new();
}

/// <summary>
/// One test against the feature or the current time. See <c>ConditionKinds</c> for the meaning of each field.
/// </summary>
public class RuleCondition
{
    public string Kind { get; set; } = string.Empty;

    /// <summary>For list kinds: matches when the feature has any of these values.</summary>
    public List<string> Values { get; set; } = new();

    /// <summary>For range kinds: start of the range (inclusive). Empty = open.</summary>
    public string From { get; set; } = string.Empty;

    /// <summary>For range kinds: end of the range (inclusive, except TimeOfDay). Empty = open.</summary>
    public string To { get; set; } = string.Empty;

    /// <summary>Invert the result ("is not").</summary>
    public bool Negate { get; set; }
}

public class PreShowStep
{
    public string Kind { get; set; } = PreShowStepKinds.Rules;

    public bool Enabled { get; set; } = true;

    public int Count { get; set; } = 1;

    /// <summary>Rules step: only use rules in this group. Empty = rules without a group.</summary>
    public string Group { get; set; } = string.Empty;

    /// <summary>Fixed step: intro keys to play, in order.</summary>
    public List<string> Intros { get; set; } = new();

    /// <summary>Trailers step: prefer trailers that share a genre with the feature.</summary>
    public bool SameGenre { get; set; } = true;

    /// <summary>Feature types this step applies to. Empty = all enabled types.</summary>
    public List<string> ApplyTo { get; set; } = new();
}

// ---- v1 types, kept so old settings files still load ----

public class IntroVideo
{
    public string Name { get; set; }

    public Guid ItemId { get; set; }
}

public interface ISpecialIntro
{
    Guid IntroId { get; set; }

    int Precedence { get; set; }

    int Prevalence { get; set; }
}

public class TagIntro : ISpecialIntro
{
    public Guid IntroId { get; set; }

    public string TagName { get; set; }

    public int Precedence { get; set; }

    public int Prevalence { get; set; }
}

public class GenreIntro : ISpecialIntro
{
    public Guid IntroId { get; set; }

    public string GenreName { get; set; }

    public int Precedence { get; set; }

    public int Prevalence { get; set; }
}

public class StudioIntro : ISpecialIntro
{
    public Guid IntroId { get; set; }

    public string StudioName { get; set; }

    public int Precedence { get; set; }

    public int Prevalence { get; set; }
}

public class DateRangeIntro : ISpecialIntro
{
    public Guid IntroId { get; set; }

    public DateTime DateStart { get; set; }

    public DateTime DateEnd { get; set; }

    public int Precedence { get; set; }

    public int Prevalence { get; set; }

    public CurrentDateRepeatRangeType RepeatType { get; set; } = CurrentDateRepeatRangeType.None;
}

public enum CurrentDateRepeatRangeType
{
    None,
    Weekly,
    Monthly,
    Yearly
}
