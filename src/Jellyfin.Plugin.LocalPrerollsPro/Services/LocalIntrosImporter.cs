using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Serialization;
using Jellyfin.Plugin.LocalPrerollsPro.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Plugins;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LocalPrerollsPro.Services;

public sealed class ImportStatus
{
    /// <summary>A Local Intros settings file exists.</summary>
    public bool Available { get; init; }

    /// <summary>The Local Intros plugin is installed and running, so both plugins would add intros.</summary>
    public bool LocalIntrosActive { get; init; }

    public string Summary { get; init; }
}

/// <summary>Copies settings from the Local Intros plugin (official v1 format or the improved fork).</summary>
public sealed class LocalIntrosImporter
{
    public static readonly Guid LocalIntrosId = Guid.Parse("07d86795-01f2-4d22-b174-cdc6056c3e7c");

    private readonly IApplicationPaths _paths;
    private readonly ILibraryManager _libraryManager;
    private readonly IPluginManager _pluginManager;
    private readonly ILogger<LocalIntrosImporter> _logger;

    public LocalIntrosImporter(IApplicationPaths paths, ILibraryManager libraryManager, IPluginManager pluginManager, ILogger<LocalIntrosImporter> logger)
    {
        _paths = paths;
        _libraryManager = libraryManager;
        _pluginManager = pluginManager;
        _logger = logger;
    }

    private string SettingsFile => Path.Combine(_paths.PluginConfigurationsPath, "Jellyfin.Plugin.LocalIntros.xml");

    public ImportStatus Status()
    {
        var active = _pluginManager.Plugins.Any(p => p.Id == LocalIntrosId && p.Manifest.Status == PluginStatus.Active);
        var source = Read();
        string summary = null;
        if (source != null)
        {
            var copy = Upgrade(source);
            summary = $"{copy.Rules.Count} rule(s), folder(s): {string.Join(", ", copy.IntroFolders)}";
        }

        return new ImportStatus { Available = source != null, LocalIntrosActive = active, Summary = summary };
    }

    /// <summary>Replaces this plugin's folders, rules and playback settings with the ones from Local Intros.</summary>
    public string Import(PluginConfiguration target)
    {
        var source = Read() ?? throw new InvalidOperationException("No Local Intros settings were found on this server.");
        var s = Upgrade(source);

        target.IntroFolders = s.IntroFolders.ToList();
        target.ScanSubfolders = s.ScanSubfolders;
        target.VideoExtensions = string.IsNullOrWhiteSpace(s.VideoExtensions) ? PluginConfiguration.DefaultVideoExtensions : s.VideoExtensions;
        target.Rules = s.Rules.Select(r =>
        {
            r.Id = Guid.NewGuid().ToString("N");
            return r;
        }).ToList();
        target.PreShow = s.PreShow.ToList();
        target.EnabledItemTypes = s.EnabledItemTypes.ToList();
        target.SelectionMode = s.SelectionMode;
        target.AvoidRepeatCount = s.AvoidRepeatCount;
        target.MinMinutesBetweenIntros = s.MinMinutesBetweenIntros;
        target.EpisodeMode = s.EpisodeMode;
        target.BingeGapMinutes = s.BingeGapMinutes;
        target.ExcludedUserIds = s.ExcludedUserIds.ToList();

        var message = $"Imported {target.Rules.Count} rule(s) and {target.IntroFolders.Count} folder(s) from Local Intros.";
        _logger.LogInformation("{Message}", message);
        return message;
    }

    private PluginConfiguration Upgrade(PluginConfiguration source)
    {
        // v1 rules point at Local Intros' own library items; their paths are still in the database.
        ConfigMigrator.Migrate(source, id => _libraryManager.GetItemById(id)?.Path);
        return source;
    }

    private PluginConfiguration Read()
    {
        if (!File.Exists(SettingsFile))
        {
            return null;
        }

        try
        {
            // Both the official plugin and the fork use the root element "IntroPluginConfiguration".
            var serializer = new XmlSerializer(typeof(PluginConfiguration), new XmlRootAttribute("IntroPluginConfiguration"));
            using var reader = XmlReader.Create(SettingsFile, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit });
            return serializer.Deserialize(reader) as PluginConfiguration;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read Local Intros settings at {File}", SettingsFile);
            return null;
        }
    }
}
