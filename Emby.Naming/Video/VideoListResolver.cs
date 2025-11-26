using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection.Metadata.Ecma335;
using System.Text.RegularExpressions;
using Emby.Naming.Common;
using Jellyfin.Data.Enums;
using Jellyfin.Extensions;
using MediaBrowser.Model.IO;
using Microsoft.AspNetCore.Mvc.Diagnostics;
using Microsoft.AspNetCore.Routing.Constraints;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.Extensions.Logging;
using Microsoft.VisualBasic;

namespace Emby.Naming.Video
{
    /// <summary>
    /// Resolves alternative versions and extras from list of video files.
    /// </summary>
    public static partial class VideoListResolver
    {
        [GeneratedRegex("[0-9]{2}[0-9]+[ip]", RegexOptions.IgnoreCase)]
        private static partial Regex ResolutionRegex();

        [GeneratedRegex(@"^\[([^]]*)\]")]
        private static partial Regex CheckMovieMultiVersionRegex();

        [GeneratedRegex(
            @"^(?<baseEpisode>.+) - (?:\[(?<Version>.+)\]|(?<Version>(?!s\d{2}e\d{2}$)[^\[\]]+))$",
            RegexOptions.IgnoreCase)]
        private static partial Regex CheckEpisodeMultiVersionRegex();

        /// <summary>
        /// Resolves alternative versions and extras from list of video files.
        /// </summary>
        /// <param name="videoInfos">List of related video files.</param>
        /// <param name="namingOptions">The naming options.</param>
        /// <param name="supportMultiVersion">Indication we should consider multi-versions of content.</param>
        /// <param name="parseName">Whether to parse the name or use the filename.</param>
        /// <param name="libraryRoot">Top-level folder for the containing library.</param>
        /// <param name="collectionType">The type of media.</param>
        /// <param name="logger">Optional logger for diagnostic messages.</param>
        /// <returns>Returns enumerable of <see cref="VideoInfo"/> which groups files together when related.</returns>
        public static IReadOnlyList<VideoInfo> Resolve(
            IReadOnlyList<VideoFileInfo> videoInfos,
            NamingOptions namingOptions,
            bool supportMultiVersion = true,
            bool parseName = true,
            string? libraryRoot = "",
            CollectionType? collectionType = 0,
            ILogger? logger = null)
        {
            // Filter out all extras, otherwise they could cause stacks to not be resolved
            // See the unit test TestStackedWithTrailer
            var nonExtras = videoInfos
                .Where(i => i.ExtraType is null)
                .Select(i => new FileSystemMetadata { FullName = i.Path, IsDirectory = i.IsDirectory });

            var stackResult = StackResolver.Resolve(nonExtras, namingOptions).ToList();

            var remainingFiles = new List<VideoFileInfo>();
            var standaloneMedia = new List<VideoFileInfo>();

            for (var i = 0; i < videoInfos.Count; i++)
            {
                var current = videoInfos[i];
                if (stackResult.Any(s => s.ContainsFile(current.Path, current.IsDirectory)))
                {
                    continue;
                }

                if (current.ExtraType is null)
                {
                    standaloneMedia.Add(current);
                }
                else
                {
                    remainingFiles.Add(current);
                }
            }

            var list = new List<VideoInfo>();

            foreach (var stack in stackResult)
            {
                var info = new VideoInfo(stack.Name)
                {
                    Files = stack.Files.Select(i => VideoResolver.Resolve(i, stack.IsDirectoryStack, namingOptions, parseName, libraryRoot))
                        .OfType<VideoFileInfo>()
                        .ToList()
                };

                info.Year = info.Files[0].Year;
                list.Add(info);
            }

            foreach (var media in standaloneMedia)
            {
                var info = new VideoInfo(media.Name) { Files = new[] { media } };

                info.Year = info.Files[0].Year;
                list.Add(info);
            }

            if (supportMultiVersion)
            {
                // do I even need namingOptions?
                if (collectionType == CollectionType.tvshows)
                {
                    list = GetShowsGroupedByVersion(list, namingOptions, logger);
                }
                else
                {
                    list = GetMoviesAndMusicVideosGroupedByVersion(list, namingOptions);
                }
            }

            // Whatever files are left, just add them
            list.AddRange(remainingFiles.Select(i => new VideoInfo(i.Name)
            {
                Files = new[] { i },
                Year = i.Year,
                ExtraType = i.ExtraType
            }));

            return list;
        }

        private static List<VideoInfo> GetMoviesAndMusicVideosGroupedByVersion(List<VideoInfo> videos, NamingOptions namingOptions)
        {
            if (videos.Count == 0)
            {
                return videos;
            }

            var folderName = Path.GetFileName(Path.GetDirectoryName(videos[0].Files[0].Path.AsSpan()));

            if (folderName.Length <= 1 || !HaveSameYear(videos))
            {
                return videos;
            }

            // Cannot use Span inside local functions and delegates thus we cannot use LINQ here nor merge with the above [if]
            VideoInfo? primary = null;
            for (var i = 0; i < videos.Count; i++)
            {
                var video = videos[i];
                if (video.ExtraType is not null)
                {
                    continue;
                }

                if (!IsMovieEligibleForMultiVersion(folderName, video.Files[0].FileNameWithoutExtension, namingOptions))
                {
                    return videos;
                }

                if (folderName.Equals(video.Files[0].FileNameWithoutExtension, StringComparison.Ordinal))
                {
                    primary = video;
                }
            }

            if (videos.Count > 1)
            {
                var groups = videos.GroupBy(x => ResolutionRegex().IsMatch(x.Files[0].FileNameWithoutExtension)).ToList();
                videos.Clear();
                foreach (var group in groups)
                {
                    if (group.Key)
                    {
                        videos.InsertRange(0, group
                            .OrderByDescending(x => ResolutionRegex().Match(x.Files[0].FileNameWithoutExtension.ToString()).Value, new AlphanumericComparator())
                            .ThenBy(x => x.Files[0].FileNameWithoutExtension.ToString(), new AlphanumericComparator()));
                    }
                    else
                    {
                        videos.AddRange(group.OrderBy(x => x.Files[0].FileNameWithoutExtension.ToString(), new AlphanumericComparator()));
                    }
                }
            }

            primary ??= videos[0];
            videos.Remove(primary);

            var list = new List<VideoInfo>
            {
                primary
            };

            list[0].AlternateVersions = videos.Select(x => x.Files[0]).ToArray();
            list[0].Name = folderName.ToString();

            return list;
        }

        private static bool HaveSameYear(IReadOnlyList<VideoInfo> videos)
        {
            if (videos.Count == 1)
            {
                return true;
            }

            var firstYear = videos[0].Year ?? -1;
            for (var i = 1; i < videos.Count; i++)
            {
                if ((videos[i].Year ?? -1) != firstYear)
                {
                    return false;
                }
            }

            return true;
        }

        private static bool IsMovieEligibleForMultiVersion(ReadOnlySpan<char> folderName, ReadOnlySpan<char> testFilename, NamingOptions namingOptions)
        {
            if (!testFilename.StartsWith(folderName, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            // Remove the folder name before cleaning as we don't care about cleaning that part
            if (folderName.Length <= testFilename.Length)
            {
                testFilename = testFilename[folderName.Length..].Trim();
            }

            // There are no span overloads for regex unfortunately
            if (CleanStringParser.TryClean(testFilename.ToString(), namingOptions.CleanStringRegexes, out var cleanName))
            {
                testFilename = cleanName.AsSpan().Trim();
            }

            // The CleanStringParser should have removed common keywords etc.
            return testFilename.IsEmpty
                   || testFilename[0] == '-'
                   || CheckMovieMultiVersionRegex().IsMatch(testFilename);
        }

        // Setup a dictionary of temporary episodeBuilder objects for each episode using the regex match for baseEpisode identifer,
        // adding all the files that are the same Episode to the Files list. Process them and build the final VideoInfo objects
        // before returning a list of VideoInfos with the versions set.
        private static List<VideoInfo> GetShowsGroupedByVersion(List<VideoInfo> videos, NamingOptions namingOptions, ILogger? logger)
        {
            if (videos.Count < 2)
            {
                return videos;
            }

            // Group all files by an "episode base key" so we don't mess with the input list.
            var groups = new Dictionary<string, (string? Name, int? Year, List<VideoFileInfo> Files)>(StringComparer.OrdinalIgnoreCase);

            // Each VideoInfo here is a single episode candidate, without any alternate versions set, and only one File Property.
            foreach (var v in videos)
            {
                if (v.Files.Count == 0)
                {
                    continue;
                }

                var (baseEpisodeKey, version) = GetEpisodeKeyAndVersion(v.Files[0]);
                // Name will be used during the sorting, so set it to the clean version match.
                if (version is not null)
                {
                    v.Files[0].Name = version;
                }

                if (!groups.TryGetValue(baseEpisodeKey, out var episodeBuilder))
                {
                    episodeBuilder = (v.Name, v.Year, new List<VideoFileInfo>());
                    groups[baseEpisodeKey] = episodeBuilder;
                }

                episodeBuilder.Files.Add(v.Files[0]);
            }

            var result = new List<VideoInfo>(groups.Count);

            foreach (var kvp in groups)
            {
                var baseEpisodeKey = kvp.Key;
                var episodeBuilder = kvp.Value;
                // TODO fix this log statemen
                // Just in case things go wrong when trying to identify groups due to an unsupported and untested naming scheme.
                if (episodeBuilder.Files.Count > 2)
                {
                    logger?.LogWarning("Found more than 2 versions for episode {EpisodeName}. This might indicate an incompatible file naming scheme.", baseEpisodeKey);
                }

                var ordered = SortEpisodeVersionFiles(episodeBuilder.Files);
                var primary = ChoosePrimaryEpisodeFile(ordered, baseEpisodeKey);

                var alternates = ordered.Where(f => !ReferenceEquals(f, primary)).ToArray();

                var completeEpisodeWithVersions = new VideoInfo(episodeBuilder.Name)
                {
                    Year = episodeBuilder.Year,
                    Files = new[] { primary },
                    AlternateVersions = alternates
                };

                result.Add(completeEpisodeWithVersions);
            }

            return result;
        }

        // Splits a filename into an "episode base key" and an optional version tag.
        private static (string BaseEpisode, string? VersionTag) GetEpisodeKeyAndVersion(VideoFileInfo file)
        {
            var name = file.FileNameWithoutExtension.ToString();
            var m = CheckEpisodeMultiVersionRegex().Match(name);

            if (m.Success)
            {
                var baseEpisode = m.Groups["baseEpisode"].Value;
                var version = m.Groups["Version"].Success ? m.Groups["Version"].Value : null;
                return (baseEpisode, version);
            }

            // No explicit version tag; the whole name is the base key.
            return (name, null);
        }

        private static List<VideoFileInfo> SortEpisodeVersionFiles(IEnumerable<VideoFileInfo> files)
        {
            var list = files.ToList();
            // No point in sorting if we only have 1 file.
            if (list.Count <= 1)
            {
                return list;
            }

            var groups = list.GroupBy(v => ResolutionRegex().IsMatch(v.Name));
            var ordered = new List<VideoFileInfo>(list.Count);

            foreach (var group in groups.OrderByDescending(g => g.Key))
            {
                if (group.Key)
                {
                    ordered.AddRange(
                        group
                            .OrderByDescending(v => ResolutionRegex().Match(v.FileNameWithoutExtension.ToString()).Value, new AlphanumericComparator())
                            .ThenBy(v => v.FileNameWithoutExtension.ToString(), new AlphanumericComparator()));
                }
                else
                {
                    ordered.AddRange(
                        group.OrderBy(v => v.FileNameWithoutExtension.ToString(), new AlphanumericComparator()));
                }
            }

            return ordered;
        }

        private static VideoFileInfo ChoosePrimaryEpisodeFile(IReadOnlyList<VideoFileInfo> orderedFiles, string baseKey)
        {
            var exact = orderedFiles.FirstOrDefault(f =>
                f.FileNameWithoutExtension.Equals(baseKey, StringComparison.OrdinalIgnoreCase));
            return exact ?? orderedFiles[0];
        }
    }
}
