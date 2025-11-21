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
        private static partial Regex CheckMultiVersionRegex();

        [GeneratedRegex(@"^(?:.*?\s*-\s*)?S\d+E\d+$", RegexOptions.IgnoreCase)]
        private static partial Regex EpisodePrimarySimpleRegex();

        /// <summary>
        /// Resolves alternative versions and extras from list of video files.
        /// </summary>
        /// <param name="videoInfos">List of related video files.</param>
        /// <param name="namingOptions">The naming options.</param>
        /// <param name="supportMultiVersion">Indication we should consider multi-versions of content.</param>
        /// <param name="parseName">Whether to parse the name or use the filename.</param>
        /// <param name="libraryRoot">Top-level folder for the containing library.</param>
        /// <param name="collectionType">The type of media.</param>
        /// <returns>Returns enumerable of <see cref="VideoInfo"/> which groups files together when related.</returns>
        public static IReadOnlyList<VideoInfo> Resolve(
            IReadOnlyList<VideoFileInfo> videoInfos,
            NamingOptions namingOptions,
            bool supportMultiVersion = true,
            bool parseName = true,
            string? libraryRoot = "",
            CollectionType? collectionType = 0)
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
                switch (collectionType)
                {
                    case CollectionType.movies:
                        list = GetMoviesGroupedByVersion(list, namingOptions);
                        break;
                    case CollectionType.tvshows:
                        list = GetShowsGroupedByVersion(list, namingOptions);
                        break;
                    default:
                        throw new InvalidOperationException($"Collection Type {collectionType} not valid for Multi Versioning");
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

        private static List<VideoInfo> GetMoviesGroupedByVersion(List<VideoInfo> videos, NamingOptions namingOptions)
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
                   || CheckMultiVersionRegex().IsMatch(testFilename);
        }

        private static List<VideoInfo> GetShowsGroupedByVersion(List<VideoInfo> videos, NamingOptions namingOptions)
        {
            // if we dont have at least two video files, then there's no point in going further.
            if (videos.Count <= 1)
            {
                return videos;
            }

            var episodesWithSortedVersions = new List<VideoInfo>();
            VideoInfo? primary = null;
            while (videos.Count > 0)
            {
                foreach (var video in videos)
                {
                    if (IsEpisodePrimary(video))
                    {
                        primary = video;
                        break;
                    }
                }

                // If there is no primary video set after scanning, the files are not named correctly for versioning.
                if (primary is null)
                {
                    return videos;
                }

                videos.Remove(primary);
                episodesWithSortedVersions.Add(FindAndSortAlternateEpisodeVersions(primary, videos));
            }

            return episodesWithSortedVersions;
        }

        private static bool IsEpisodePrimary(VideoInfo video)
        {
            // This would probably be easier if video.Name was used, but currently the tryclean string regexes chop out some resolutions, and changing them risks breaking other things
            // so we must use the raw VideoFileInfo.FileNameWithoutExtension
            var match = EpisodePrimarySimpleRegex().IsMatch(video.Files[0].FileNameWithoutExtension.Trim());

            return match;
        }

// Placeholder
        private static VideoInfo FindAndSortAlternateEpisodeVersions(VideoInfo primary, List<VideoInfo> videos)
        {
            return primary;
// comment
//           if (videos.Count > 1)
//            {
//                var groups = videos.GroupBy(x => ResolutionRegex().IsMatch(x.Files[0].FileNameWithoutExtension)).ToList();
//                videos.Clear();
//                foreach (var group in groups)
//                {
//                    if (group.Key)
//                    {
//                        videos.InsertRange(0, group
//                            .OrderByDescending(x => ResolutionRegex().Match(x.Files[0].FileNameWithoutExtension.ToString()).Value, new AlphanumericComparator())
//                            .ThenBy(x => x.Files[0].FileNameWithoutExtension.ToString(), new AlphanumericComparator()));
//                    }
//                    else
//                    {
//                        videos.AddRange(group.OrderBy(x => x.Files[0].FileNameWithoutExtension.ToString(), new AlphanumericComparator()));
//                    }
//                }
//            }
//
//            primary ??= videos[0];
//            videos.Remove(primary);
//
//            var list = new List<VideoInfo>
//            {
//                primary
//            };
//
//            list[0].AlternateVersions = videos.Select(x => x.Files[0]).ToArray();
//            list[0].Name = folderName.ToString();
        }
    }
}
