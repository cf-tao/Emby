﻿using MediaBrowser.Common.Extensions;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.MediaInfo;
using MediaBrowser.Model.Serialization;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Threading;
using MediaBrowser.Model.Dlna;

namespace Emby.Server.Implementations.Library
{
    public class MediaSourceManager : IMediaSourceManager, IDisposable
    {
        private readonly IItemRepository _itemRepo;
        private readonly IUserManager _userManager;
        private readonly ILibraryManager _libraryManager;
        private readonly IJsonSerializer _jsonSerializer;
        private readonly IFileSystem _fileSystem;

        private IMediaSourceProvider[] _providers;
        private readonly ILogger _logger;
        private readonly IUserDataManager _userDataManager;
        private readonly ITimerFactory _timerFactory;
        private readonly Func<IMediaEncoder> _mediaEncoder;

        public MediaSourceManager(IItemRepository itemRepo, IUserManager userManager, ILibraryManager libraryManager, ILogger logger, IJsonSerializer jsonSerializer, IFileSystem fileSystem, IUserDataManager userDataManager, ITimerFactory timerFactory, Func<IMediaEncoder> mediaEncoder)
        {
            _itemRepo = itemRepo;
            _userManager = userManager;
            _libraryManager = libraryManager;
            _logger = logger;
            _jsonSerializer = jsonSerializer;
            _fileSystem = fileSystem;
            _userDataManager = userDataManager;
            _timerFactory = timerFactory;
            _mediaEncoder = mediaEncoder;
        }

        public void AddParts(IEnumerable<IMediaSourceProvider> providers)
        {
            _providers = providers.ToArray();
        }

        public List<MediaStream> GetMediaStreams(MediaStreamQuery query)
        {
            var list = _itemRepo.GetMediaStreams(query);

            foreach (var stream in list)
            {
                stream.SupportsExternalStream = StreamSupportsExternalStream(stream);
            }

            return list;
        }

        private bool StreamSupportsExternalStream(MediaStream stream)
        {
            if (stream.IsExternal)
            {
                return true;
            }

            if (stream.IsTextSubtitleStream)
            {
                return true;
            }

            return false;
        }

        public List<MediaStream> GetMediaStreams(string mediaSourceId)
        {
            var list = GetMediaStreams(new MediaStreamQuery
            {
                ItemId = new Guid(mediaSourceId)
            });

            return GetMediaStreamsForItem(list);
        }

        public List<MediaStream> GetMediaStreams(Guid itemId)
        {
            var list = GetMediaStreams(new MediaStreamQuery
            {
                ItemId = itemId
            });

            return GetMediaStreamsForItem(list);
        }

        private List<MediaStream> GetMediaStreamsForItem(List<MediaStream> streams)
        {
            foreach (var stream in streams)
            {
                if (stream.Type == MediaStreamType.Subtitle)
                {
                    stream.SupportsExternalStream = StreamSupportsExternalStream(stream);
                }
            }

            return streams;
        }

        public async Task<IEnumerable<MediaSourceInfo>> GetPlayackMediaSources(string id, string userId, bool enablePathSubstitution, string[] supportedLiveMediaTypes, CancellationToken cancellationToken)
        {
            var item = _libraryManager.GetItemById(id);

            var hasMediaSources = (IHasMediaSources)item;
            User user = null;

            if (!string.IsNullOrEmpty(userId))
            {
                user = _userManager.GetUserById(userId);
            }

            var mediaSources = GetStaticMediaSources(hasMediaSources, enablePathSubstitution, user);

            if (mediaSources.Count == 1 && mediaSources[0].Type != MediaSourceType.Placeholder && !mediaSources[0].MediaStreams.Any(i => i.Type == MediaStreamType.Audio || i.Type == MediaStreamType.Video))
            {
                await item.RefreshMetadata(new MediaBrowser.Controller.Providers.MetadataRefreshOptions(_fileSystem)
                {
                    EnableRemoteContentProbe = true,
                    MetadataRefreshMode = MediaBrowser.Controller.Providers.MetadataRefreshMode.FullRefresh

                }, cancellationToken).ConfigureAwait(false);

                mediaSources = GetStaticMediaSources(hasMediaSources, enablePathSubstitution, user);
            }

            var dynamicMediaSources = await GetDynamicMediaSources(hasMediaSources, cancellationToken).ConfigureAwait(false);

            var list = new List<MediaSourceInfo>();

            list.AddRange(mediaSources);

            foreach (var source in dynamicMediaSources)
            {
                if (user != null)
                {
                    SetUserProperties(hasMediaSources as BaseItem, source, user);
                }
                if (source.Protocol == MediaProtocol.File || source.Protocol == MediaProtocol.Http)
                {
                    // trust whatever was set by the media source provider
                }
                else
                {
                    source.SupportsDirectStream = false;
                }

                list.Add(source);
            }

            foreach (var source in list)
            {
                if (user != null)
                {
                    if (string.Equals(item.MediaType, MediaType.Audio, StringComparison.OrdinalIgnoreCase))
                    {
                        if (!user.Policy.EnableAudioPlaybackTranscoding)
                        {
                            source.SupportsTranscoding = false;
                        }
                    }
                }
            }

            return SortMediaSources(list).Where(i => i.Type != MediaSourceType.Placeholder);
        }

        private async Task<IEnumerable<MediaSourceInfo>> GetDynamicMediaSources(IHasMediaSources item, CancellationToken cancellationToken)
        {
            var tasks = _providers.Select(i => GetDynamicMediaSources(item, i, cancellationToken));
            var results = await Task.WhenAll(tasks).ConfigureAwait(false);

            return results.SelectMany(i => i.ToList());
        }

        private async Task<IEnumerable<MediaSourceInfo>> GetDynamicMediaSources(IHasMediaSources item, IMediaSourceProvider provider, CancellationToken cancellationToken)
        {
            try
            {
                var sources = await provider.GetMediaSources(item, cancellationToken).ConfigureAwait(false);
                var list = sources.ToList();

                foreach (var mediaSource in list)
                {
                    mediaSource.InferTotalBitrate();

                    SetKeyProperties(provider, mediaSource);
                }

                return list;
            }
            catch (Exception ex)
            {
                _logger.ErrorException("Error getting media sources", ex);
                return new List<MediaSourceInfo>();
            }
        }

        private void SetKeyProperties(IMediaSourceProvider provider, MediaSourceInfo mediaSource)
        {
            var prefix = provider.GetType().FullName.GetMD5().ToString("N") + LiveStreamIdDelimeter;

            if (!string.IsNullOrEmpty(mediaSource.OpenToken) && !mediaSource.OpenToken.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                mediaSource.OpenToken = prefix + mediaSource.OpenToken;
            }

            if (!string.IsNullOrEmpty(mediaSource.LiveStreamId) && !mediaSource.LiveStreamId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                mediaSource.LiveStreamId = prefix + mediaSource.LiveStreamId;
            }
        }

        public async Task<MediaSourceInfo> GetMediaSource(IHasMediaSources item, string mediaSourceId, string liveStreamId, bool enablePathSubstitution, CancellationToken cancellationToken)
        {
            if (!string.IsNullOrEmpty(liveStreamId))
            {
                return await GetLiveStream(liveStreamId, cancellationToken).ConfigureAwait(false);
            }

            var sources = await GetPlayackMediaSources(item.Id.ToString("N"), null, enablePathSubstitution, new[] { MediaType.Audio, MediaType.Video },
                        CancellationToken.None).ConfigureAwait(false);

            return sources.FirstOrDefault(i => string.Equals(i.Id, mediaSourceId, StringComparison.OrdinalIgnoreCase));
        }

        public List<MediaSourceInfo> GetStaticMediaSources(IHasMediaSources item, bool enablePathSubstitution, User user = null)
        {
            if (item == null)
            {
                throw new ArgumentNullException("item");
            }

            if (!(item is Video))
            {
                return item.GetMediaSources(enablePathSubstitution);
            }

            var sources = item.GetMediaSources(enablePathSubstitution);

            if (user != null)
            {
                foreach (var source in sources)
                {
                    SetUserProperties(item as BaseItem, source, user);
                }
            }

            return sources;
        }

        private void SetUserProperties(BaseItem item, MediaSourceInfo source, User user)
        {
            var userData = item == null ? new UserItemData() : _userDataManager.GetUserData(user, item);

            var allowRememberingSelection = item == null || item.EnableRememberingTrackSelections;

            SetDefaultAudioStreamIndex(source, userData, user, allowRememberingSelection);
            SetDefaultSubtitleStreamIndex(source, userData, user, allowRememberingSelection);
        }

        private void SetDefaultSubtitleStreamIndex(MediaSourceInfo source, UserItemData userData, User user, bool allowRememberingSelection)
        {
            if (userData.SubtitleStreamIndex.HasValue && user.Configuration.RememberSubtitleSelections && user.Configuration.SubtitleMode != SubtitlePlaybackMode.None && allowRememberingSelection)
            {
                var index = userData.SubtitleStreamIndex.Value;
                // Make sure the saved index is still valid
                if (index == -1 || source.MediaStreams.Any(i => i.Type == MediaStreamType.Subtitle && i.Index == index))
                {
                    source.DefaultSubtitleStreamIndex = index;
                    return;
                }
            }

            var preferredSubs = string.IsNullOrEmpty(user.Configuration.SubtitleLanguagePreference)
                ? new string[] { } : new string[] { user.Configuration.SubtitleLanguagePreference };

            var defaultAudioIndex = source.DefaultAudioStreamIndex;
            var audioLangage = defaultAudioIndex == null
                ? null
                : source.MediaStreams.Where(i => i.Type == MediaStreamType.Audio && i.Index == defaultAudioIndex).Select(i => i.Language).FirstOrDefault();

            source.DefaultSubtitleStreamIndex = MediaStreamSelector.GetDefaultSubtitleStreamIndex(source.MediaStreams,
                preferredSubs,
                user.Configuration.SubtitleMode,
                audioLangage);

            MediaStreamSelector.SetSubtitleStreamScores(source.MediaStreams, preferredSubs,
                user.Configuration.SubtitleMode, audioLangage);
        }

        private void SetDefaultAudioStreamIndex(MediaSourceInfo source, UserItemData userData, User user, bool allowRememberingSelection)
        {
            if (userData.AudioStreamIndex.HasValue && user.Configuration.RememberAudioSelections && allowRememberingSelection)
            {
                var index = userData.AudioStreamIndex.Value;
                // Make sure the saved index is still valid
                if (source.MediaStreams.Any(i => i.Type == MediaStreamType.Audio && i.Index == index))
                {
                    source.DefaultAudioStreamIndex = index;
                    return;
                }
            }

            var preferredAudio = string.IsNullOrEmpty(user.Configuration.AudioLanguagePreference)
                ? new string[] { }
                : new[] { user.Configuration.AudioLanguagePreference };

            source.DefaultAudioStreamIndex = MediaStreamSelector.GetDefaultAudioStreamIndex(source.MediaStreams, preferredAudio, user.Configuration.PlayDefaultAudioTrack);
        }

        private IEnumerable<MediaSourceInfo> SortMediaSources(IEnumerable<MediaSourceInfo> sources)
        {
            return sources.OrderBy(i =>
            {
                if (i.VideoType.HasValue && i.VideoType.Value == VideoType.VideoFile)
                {
                    return 0;
                }

                return 1;

            }).ThenBy(i => i.Video3DFormat.HasValue ? 1 : 0)
            .ThenByDescending(i =>
            {
                var stream = i.VideoStream;

                return stream == null || stream.Width == null ? 0 : stream.Width.Value;
            })
            .ToList();
        }

        private readonly Dictionary<string, LiveStreamInfo> _openStreams = new Dictionary<string, LiveStreamInfo>(StringComparer.OrdinalIgnoreCase);
        private readonly SemaphoreSlim _liveStreamSemaphore = new SemaphoreSlim(1, 1);

        public async Task<LiveStreamResponse> OpenLiveStream(LiveStreamRequest request, CancellationToken cancellationToken)
        {
            await _liveStreamSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                var tuple = GetProvider(request.OpenToken);
                var provider = tuple.Item1;

                var mediaSourceTuple = await provider.OpenMediaSource(tuple.Item2, cancellationToken).ConfigureAwait(false);

                var mediaSource = mediaSourceTuple.Item1;

                if (string.IsNullOrEmpty(mediaSource.LiveStreamId))
                {
                    throw new InvalidOperationException(string.Format("{0} returned null LiveStreamId", provider.GetType().Name));
                }

                SetKeyProperties(provider, mediaSource);

                var info = new LiveStreamInfo
                {
                    Id = mediaSource.LiveStreamId,
                    MediaSource = mediaSource,
                    DirectStreamProvider = mediaSourceTuple.Item2,
                    AllowLiveMediaInfoProbe = mediaSourceTuple.Item3
                };

                _openStreams[mediaSource.LiveStreamId] = info;

                var json = _jsonSerializer.SerializeToString(mediaSource);
                _logger.Debug("Live stream opened: " + json);
                var clone = _jsonSerializer.DeserializeFromString<MediaSourceInfo>(json);

                if (!string.IsNullOrEmpty(request.UserId))
                {
                    var user = _userManager.GetUserById(request.UserId);
                    var item = string.IsNullOrEmpty(request.ItemId)
                        ? null
                        : _libraryManager.GetItemById(request.ItemId);
                    SetUserProperties(item, clone, user);
                }

                return new LiveStreamResponse
                {
                    MediaSource = clone
                };
            }
            finally
            {
                _liveStreamSemaphore.Release();
            }
        }

        public async Task<MediaSourceInfo> GetLiveStreamMediaInfo(string id, CancellationToken cancellationToken)
        {
            var liveStreamInfo = await GetLiveStreamInfo(id, cancellationToken).ConfigureAwait(false);

            var mediaSource = liveStreamInfo.MediaSource;

            if (liveStreamInfo.AllowLiveMediaInfoProbe)
            {
                var info = await _mediaEncoder().GetMediaInfo(new MediaInfoRequest
                {
                    MediaSource = mediaSource,
                    ExtractChapters = false,
                    MediaType = DlnaProfileType.Video

                }, cancellationToken).ConfigureAwait(false);

                mediaSource.MediaStreams = info.MediaStreams;
                mediaSource.Container = info.Container;
                mediaSource.Bitrate = info.Bitrate;
            }

            return mediaSource;
        }

        public async Task<Tuple<MediaSourceInfo, IDirectStreamProvider>> GetLiveStreamWithDirectStreamProvider(string id, CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(id))
            {
                throw new ArgumentNullException("id");
            }

            var info = await GetLiveStreamInfo(id, cancellationToken).ConfigureAwait(false);
            return new Tuple<MediaSourceInfo, IDirectStreamProvider>(info.MediaSource, info.DirectStreamProvider);
        }

        private async Task<LiveStreamInfo> GetLiveStreamInfo(string id, CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(id))
            {
                throw new ArgumentNullException("id");
            }

            await _liveStreamSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                LiveStreamInfo info;
                if (_openStreams.TryGetValue(id, out info))
                {
                    return info;
                }
                else
                {
                    throw new ResourceNotFoundException();
                }
            }
            finally
            {
                _liveStreamSemaphore.Release();
            }
        }

        public async Task<MediaSourceInfo> GetLiveStream(string id, CancellationToken cancellationToken)
        {
            var result = await GetLiveStreamWithDirectStreamProvider(id, cancellationToken).ConfigureAwait(false);
            return result.Item1;
        }

        private async Task CloseLiveStreamWithProvider(IMediaSourceProvider provider, string streamId)
        {
            _logger.Info("Closing live stream {0} with provider {1}", streamId, provider.GetType().Name);

            try
            {
                await provider.CloseMediaSource(streamId).ConfigureAwait(false);
            }
            catch (NotImplementedException)
            {
            }
            catch (Exception ex)
            {
                _logger.ErrorException("Error closing live stream {0}", ex, streamId);
            }
        }

        public async Task CloseLiveStream(string id)
        {
            if (string.IsNullOrEmpty(id))
            {
                throw new ArgumentNullException("id");
            }

            await _liveStreamSemaphore.WaitAsync().ConfigureAwait(false);

            try
            {
                LiveStreamInfo current;

                if (_openStreams.TryGetValue(id, out current))
                {
                    _openStreams.Remove(id);
                    current.Closed = true;

                    if (current.MediaSource.RequiresClosing)
                    {
                        var tuple = GetProvider(id);

                        await CloseLiveStreamWithProvider(tuple.Item1, tuple.Item2).ConfigureAwait(false);
                    }
                }
            }
            finally
            {
                _liveStreamSemaphore.Release();
            }
        }

        // Do not use a pipe here because Roku http requests to the server will fail, without any explicit error message.
        private const char LiveStreamIdDelimeter = '_';

        private Tuple<IMediaSourceProvider, string> GetProvider(string key)
        {
            if (string.IsNullOrEmpty(key))
            {
                throw new ArgumentException("key");
            }

            var keys = key.Split(new[] { LiveStreamIdDelimeter }, 2);

            var provider = _providers.FirstOrDefault(i => string.Equals(i.GetType().FullName.GetMD5().ToString("N"), keys[0], StringComparison.OrdinalIgnoreCase));

            var splitIndex = key.IndexOf(LiveStreamIdDelimeter);
            var keyId = key.Substring(splitIndex + 1);

            return new Tuple<IMediaSourceProvider, string>(provider, keyId);
        }

        /// <summary>
        /// Performs application-defined tasks associated with freeing, releasing, or resetting unmanaged resources.
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        private readonly object _disposeLock = new object();
        /// <summary>
        /// Releases unmanaged and - optionally - managed resources.
        /// </summary>
        /// <param name="dispose"><c>true</c> to release both managed and unmanaged resources; <c>false</c> to release only unmanaged resources.</param>
        protected virtual void Dispose(bool dispose)
        {
            if (dispose)
            {
                lock (_disposeLock)
                {
                    foreach (var key in _openStreams.Keys.ToList())
                    {
                        var task = CloseLiveStream(key);

                        Task.WaitAll(task);
                    }
                }
            }
        }

        private class LiveStreamInfo
        {
            public string Id;
            public bool Closed;
            public MediaSourceInfo MediaSource;
            public IDirectStreamProvider DirectStreamProvider;
            public bool AllowLiveMediaInfoProbe { get; set; }
        }
    }
}