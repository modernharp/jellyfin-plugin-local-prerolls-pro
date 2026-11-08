using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Jellyfin.Plugin.LocalPrerollsPro.Engine;

/// <summary>
/// An intro's key is its path relative to the intro folder it was found in, with "/" separators
/// (e.g. "holidays/xmas.mp4"). Keys survive rescans, database resets and moving the whole folder.
/// </summary>
public static class IntroKeys
{
    public static string FromPath(string fullPath, IEnumerable<string> roots)
    {
        foreach (var root in roots ?? Enumerable.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(root))
            {
                continue;
            }

            var r = root.Trim();
            if (string.Equals(Normalize(r), Normalize(fullPath), StringComparison.OrdinalIgnoreCase))
            {
                // The root is the file itself (v1 allowed a single file).
                return Path.GetFileName(fullPath);
            }

            var rel = Path.GetRelativePath(r, fullPath);
            if (!rel.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(rel))
            {
                return rel.Replace('\\', '/');
            }
        }

        return Path.GetFileName(fullPath);
    }

    public static ISet<string> ParseExtensions(string extensions)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in (extensions ?? string.Empty).Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
        {
            set.Add(part.StartsWith('.') ? part : "." + part);
        }

        return set;
    }

    /// <summary>Folder part of a key ("holidays/" for "holidays/xmas.mp4"), or empty for the root.</summary>
    public static string FolderOf(string key)
    {
        var i = key.LastIndexOf('/');
        return i < 0 ? string.Empty : key[..(i + 1)];
    }

    /// <summary>Readable name from a file name: "jellyfin-pre-roll_Neon_City" becomes "Neon City".</summary>
    public static string DisplayName(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path)
            .Replace("jellyfin", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("pre-roll", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("preroll", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace('_', ' ')
            .Replace('-', ' ')
            .Replace('.', ' ');
        name = string.Join(' ', name.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return name.Length > 0 ? name : Path.GetFileNameWithoutExtension(path);
    }

    private static string Normalize(string path) => path.TrimEnd('/', '\\');
}
