// This file is part of the DSharpPlus project.
//
// Copyright (c) 2015 Mike Santiago
// Copyright (c) 2016-2023 DSharpPlus Contributors
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in all
// copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
// SOFTWARE.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using DSharpPlus.AsyncEvents;
using DSharpPlus.Entities;
using DSharpPlus.Enums;
using DSharpPlus.EventArgs;
using DSharpPlus.Exceptions;
using DSharpPlus.Net;
using DSharpPlus.Net.Abstractions;
using DSharpPlus.Net.Models;
using DSharpPlus.Net.Serialization;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace DSharpPlus
{
    /// <summary>
    /// A Discord API wrapper.
    /// </summary>
    public sealed partial class DiscordClient : BaseDiscordClient
    {
        #region Internal Fields/Properties

        internal bool _isShard = false;
        internal RingBuffer<DiscordMessage> MessageCache { get; }

        private List<BaseExtension> _extensions = new();
        private StatusUpdate _status = null;

        private ManualResetEventSlim ConnectionLock { get; } = new ManualResetEventSlim(true);

        #endregion

        #region Public Fields/Properties
        /// <summary>
        /// Gets the gateway protocol version.
        /// </summary>
        public int GatewayVersion { get; internal set; }

        /// <summary>
        /// Gets the gateway session information for this client.
        /// </summary>
        public GatewayInfo GatewayInfo { get; internal set; }

        /// <summary>
        /// Gets the gateway URL.
        /// </summary>
        public Uri GatewayUri { get; internal set; }

        /// <summary>
        /// Gets the total number of shards the bot is connected to.
        /// </summary>
        public int ShardCount => this.GatewayInfo != null
            ? this.GatewayInfo.ShardCount
            : this.Configuration.ShardCount;

        /// <summary>
        /// Gets the currently connected shard ID.
        /// </summary>
        public int ShardId
            => this.Configuration.ShardId;

        /// <summary>
        /// Gets the intents configured for this client.
        /// </summary>
        public DiscordIntents Intents
            => this.Configuration.Intents;

        /// <summary>
        /// Gets a dictionary of DM channels that have been cached by this client. The dictionary's key is the channel
        /// ID.
        /// </summary>
        public IReadOnlyDictionary<ulong, DiscordDmChannel> PrivateChannels { get; }
        internal ConcurrentDictionary<ulong, DiscordDmChannel> _privateChannels = new();

        /// <summary>
        /// Gets a dictionary of guilds that this client is in. The dictionary's key is the guild ID. Note that the
        /// guild objects in this dictionary will not be filled in if the specific guilds aren't available (the
        /// <see cref="GuildAvailable"/> or <see cref="GuildDownloadCompleted"/> events haven't been fired yet)
        /// </summary>
        public override IReadOnlyDictionary<ulong, DiscordGuild> Guilds { get; }
        internal ConcurrentDictionary<ulong, DiscordGuild> _guilds = new();


        /// <summary>
        /// Gets a collection of relationships held by this client.
        /// </summary>
        public IReadOnlyDictionary<ulong, DiscordRelationship> Relationships { get; }
        internal ConcurrentDictionary<ulong, DiscordRelationship> _relationships = new();

        /// <summary>
        /// Gets the WS latency for this client.
        /// </summary>
        public int Ping
            => Volatile.Read(ref this._ping);

        private int _ping;

        public DiscordUserSettings UserSettings { get; private set; } = new();

        public string UserSettingsProto { get; private set; } = string.Empty;

        /// <summary>
        /// Gets the collection of presences held by this client.
        /// </summary>
        public IReadOnlyDictionary<ulong, DiscordPresence> Presences { get; }

        internal ConcurrentDictionary<ulong, DiscordPresence> _presences = new();

        /// <summary>
        /// Gets the collection of user guild settings held by this client.
        /// </summary>
        public IReadOnlyDictionary<ulong, DiscordUserGuildSettings> UserGuildSettings { get; }
        internal ConcurrentDictionary<ulong, DiscordUserGuildSettings> _userGuildSettings = new();

        public IReadOnlyDictionary<ulong, DiscordReadState> ReadStates { get; }
        internal ConcurrentDictionary<ulong, DiscordReadState>_readStates = new();

        public int UserCacheCount
            => this.UserCache.Count;

        #endregion

        #region Constructor/Internal Setup

        /// <summary>
        /// Initializes a new instance of DiscordClient.
        /// </summary>
        /// <param name="config">Specifies configuration parameters.</param>
        public DiscordClient(DiscordConfiguration config)
            : base(config)
        {
            if (this.Configuration.MessageCacheSize > 0)
            {
                this.MessageCache = new RingBuffer<DiscordMessage>(this.Configuration.MessageCacheSize);
            }

            this.InternalSetup();

            this.Guilds = new ReadOnlyConcurrentDictionary<ulong, DiscordGuild>(this._guilds);
            this.PrivateChannels = new ReadOnlyConcurrentDictionary<ulong, DiscordDmChannel>(this._privateChannels);
            this.Relationships = new ReadOnlyConcurrentDictionary<ulong, DiscordRelationship>(this._relationships);
            this.Presences = new ReadOnlyConcurrentDictionary<ulong, DiscordPresence>(this._presences);
            this.ReadStates = new ReadOnlyConcurrentDictionary<ulong, DiscordReadState>(this._readStates);
            this.UserGuildSettings = new ReadOnlyConcurrentDictionary<ulong, DiscordUserGuildSettings>(this._userGuildSettings);
        }

        /// <summary>
        /// This constructor is used when constructing a sharded client to use a shared rest client.
        /// </summary>
        /// <param name="config">Specifies configuration parameters.</param>
        /// <param name="restClient">Restclient which will be used for the underlying ApiClients</param>
        internal DiscordClient(DiscordConfiguration config, RestClient restClient)
            : base(config, restClient)
        {
            if (this.Configuration.MessageCacheSize > 0)
            {
                var intents = this.Configuration.Intents;
                this.MessageCache = intents.HasIntent(DiscordIntents.GuildMessages) || intents.HasIntent(DiscordIntents.DirectMessages)
                        ? new RingBuffer<DiscordMessage>(this.Configuration.MessageCacheSize)
                        : null;
            }

            this.InternalSetup();

            this.Guilds = new ReadOnlyConcurrentDictionary<ulong, DiscordGuild>(this._guilds);
            this.PrivateChannels = new ReadOnlyConcurrentDictionary<ulong, DiscordDmChannel>(this._privateChannels);
        }

        internal void InternalSetup()
        {
            this._clientErrored = new AsyncEvent<DiscordClient, ClientErrorEventArgs>("CLIENT_ERRORED", this.Goof);
            this._socketErrored = new AsyncEvent<DiscordClient, SocketErrorEventArgs>("SOCKET_ERRORED", this.Goof);
            this._socketOpened = new AsyncEvent<DiscordClient, SocketEventArgs>("SOCKET_OPENED", this.EventErrorHandler);
            this._socketClosed = new AsyncEvent<DiscordClient, SocketCloseEventArgs>("SOCKET_CLOSED", this.EventErrorHandler);
            this._ready = new AsyncEvent<DiscordClient, ReadyEventArgs>("READY", this.EventErrorHandler);
            this._resumed = new AsyncEvent<DiscordClient, ResumedEventArgs>("RESUMED", this.EventErrorHandler);
            this._channelCreated = new AsyncEvent<DiscordClient, ChannelCreateEventArgs>("CHANNEL_CREATED", this.EventErrorHandler);
            this._channelUpdated = new AsyncEvent<DiscordClient, ChannelUpdateEventArgs>("CHANNEL_UPDATED", this.EventErrorHandler);
            this._channelDeleted = new AsyncEvent<DiscordClient, ChannelDeleteEventArgs>("CHANNEL_DELETED", this.EventErrorHandler);
            this._channelUnreadUpdate = new AsyncEvent<DiscordClient, ChannelUnreadUpdateEventArgs>("CHANNEL_UNREAD_UPDATED", this.EventErrorHandler);
            this._channelRecipientAdded = new AsyncEvent<DiscordClient, ChannelRecipientAddedEventArgs>("CHANNEL_RECIPIENT_ADD", this.EventErrorHandler);
            this._channelRecipientRemoved = new AsyncEvent<DiscordClient, ChannelRecipientRemovedEventArgs>("CHANNEL_RECIPIENT_REMOVE", this.EventErrorHandler);
            this._dmChannelCreated = new AsyncEvent<DiscordClient, DmChannelCreateEventArgs>("DM_CHANNEL_CREATED", this.EventErrorHandler);
            this._dmChannelDeleted = new AsyncEvent<DiscordClient, DmChannelDeleteEventArgs>("DM_CHANNEL_DELETED", this.EventErrorHandler);
            this._channelPinsUpdated = new AsyncEvent<DiscordClient, ChannelPinsUpdateEventArgs>("CHANNEL_PINS_UPDATED", this.EventErrorHandler);
            this._guildCreated = new AsyncEvent<DiscordClient, GuildCreateEventArgs>("GUILD_CREATED", this.EventErrorHandler);
            this._guildAvailable = new AsyncEvent<DiscordClient, GuildCreateEventArgs>("GUILD_AVAILABLE", this.EventErrorHandler);
            this._guildUpdated = new AsyncEvent<DiscordClient, GuildUpdateEventArgs>("GUILD_UPDATED", this.EventErrorHandler);
            this._guildDeleted = new AsyncEvent<DiscordClient, GuildDeleteEventArgs>("GUILD_DELETED", this.EventErrorHandler);
            this._guildUnavailable = new AsyncEvent<DiscordClient, GuildDeleteEventArgs>("GUILD_UNAVAILABLE", this.EventErrorHandler);
            this._guildDownloadCompletedEv = new AsyncEvent<DiscordClient, GuildDownloadCompletedEventArgs>("GUILD_DOWNLOAD_COMPLETED", this.EventErrorHandler);
            this._inviteCreated = new AsyncEvent<DiscordClient, InviteCreateEventArgs>("INVITE_CREATED", this.EventErrorHandler);
            this._inviteDeleted = new AsyncEvent<DiscordClient, InviteDeleteEventArgs>("INVITE_DELETED", this.EventErrorHandler);
            this._messageCreated = new AsyncEvent<DiscordClient, MessageCreateEventArgs>("MESSAGE_CREATED", this.EventErrorHandler);
            this._presenceUpdated = new AsyncEvent<DiscordClient, PresenceUpdateEventArgs>("PRESENCE_UPDATED", this.EventErrorHandler);
            this._scheduledGuildEventCreated = new AsyncEvent<DiscordClient, ScheduledGuildEventCreateEventArgs>("SCHEDULED_GUILD_EVENT_CREATED", this.EventErrorHandler);
            this._scheduledGuildEventDeleted = new AsyncEvent<DiscordClient, ScheduledGuildEventDeleteEventArgs>("SCHEDULED_GUILD_EVENT_DELETED", this.EventErrorHandler);
            this._scheduledGuildEventUpdated = new AsyncEvent<DiscordClient, ScheduledGuildEventUpdateEventArgs>("SCHEDULED_GUILD_EVENT_UPDATED", this.EventErrorHandler);
            this._scheduledGuildEventCompleted = new AsyncEvent<DiscordClient, ScheduledGuildEventCompletedEventArgs>("SCHEDULED_GUILD_EVENT_COMPLETED", this.EventErrorHandler);
            this._scheduledGuildEventUserAdded = new AsyncEvent<DiscordClient, ScheduledGuildEventUserAddEventArgs>("SCHEDULED_GUILD_EVENT_USER_ADDED", this.EventErrorHandler);
            this._scheduledGuildEventUserRemoved = new AsyncEvent<DiscordClient, ScheduledGuildEventUserRemoveEventArgs>("SCHEDULED_GUILD_EVENT_USER_REMOVED", this.EventErrorHandler);
            this._guildBanAdded = new AsyncEvent<DiscordClient, GuildBanAddEventArgs>("GUILD_BAN_ADD", this.EventErrorHandler);
            this._guildBanRemoved = new AsyncEvent<DiscordClient, GuildBanRemoveEventArgs>("GUILD_BAN_REMOVED", this.EventErrorHandler);
            this._guildEmojisUpdated = new AsyncEvent<DiscordClient, GuildEmojisUpdateEventArgs>("GUILD_EMOJI_UPDATED", this.EventErrorHandler);
            this._guildStickersUpdated = new AsyncEvent<DiscordClient, GuildStickersUpdateEventArgs>("GUILD_STICKER_UPDATED", this.EventErrorHandler);
            this._guildIntegrationsUpdated = new AsyncEvent<DiscordClient, GuildIntegrationsUpdateEventArgs>("GUILD_INTEGRATIONS_UPDATED", this.EventErrorHandler);
            this._guildMemberAdded = new AsyncEvent<DiscordClient, GuildMemberAddEventArgs>("GUILD_MEMBER_ADD", this.EventErrorHandler);
            this._guildMemberRemoved = new AsyncEvent<DiscordClient, GuildMemberRemoveEventArgs>("GUILD_MEMBER_REMOVED", this.EventErrorHandler);
            this._guildMemberUpdated = new AsyncEvent<DiscordClient, GuildMemberUpdateEventArgs>("GUILD_MEMBER_UPDATED", this.EventErrorHandler);
            this._guildRoleCreated = new AsyncEvent<DiscordClient, GuildRoleCreateEventArgs>("GUILD_ROLE_CREATED", this.EventErrorHandler);
            this._guildRoleUpdated = new AsyncEvent<DiscordClient, GuildRoleUpdateEventArgs>("GUILD_ROLE_UPDATED", this.EventErrorHandler);
            this._guildRoleDeleted = new AsyncEvent<DiscordClient, GuildRoleDeleteEventArgs>("GUILD_ROLE_DELETED", this.EventErrorHandler);
            this._messageAcknowledged = new AsyncEvent<DiscordClient, MessageAcknowledgeEventArgs>("MESSAGE_ACKNOWLEDGED", this.EventErrorHandler);
            this._messageUpdated = new AsyncEvent<DiscordClient, MessageUpdateEventArgs>("MESSAGE_UPDATED", this.EventErrorHandler);
            this._messageDeleted = new AsyncEvent<DiscordClient, MessageDeleteEventArgs>("MESSAGE_DELETED", this.EventErrorHandler);
            this._messagesBulkDeleted = new AsyncEvent<DiscordClient, MessageBulkDeleteEventArgs>("MESSAGE_BULK_DELETED", this.EventErrorHandler);
            this._interactionCreated = new AsyncEvent<DiscordClient, InteractionCreateEventArgs>("INTERACTION_CREATED", this.EventErrorHandler);
            this._componentInteractionCreated = new AsyncEvent<DiscordClient, ComponentInteractionCreateEventArgs>("COMPONENT_INTERACTED", this.EventErrorHandler);
            this._modalSubmitted = new AsyncEvent<DiscordClient, ModalSubmitEventArgs>("MODAL_SUBMITTED", this.EventErrorHandler);
            this._contextMenuInteractionCreated = new AsyncEvent<DiscordClient, ContextMenuInteractionCreateEventArgs>("CONTEXT_MENU_INTERACTED", this.EventErrorHandler);
            this._typingStarted = new AsyncEvent<DiscordClient, TypingStartEventArgs>("TYPING_STARTED", this.EventErrorHandler);
            this._userSettingsUpdated = new AsyncEvent<DiscordClient, UserSettingsUpdateEventArgs>("USER_SETTINGS_UPDATED", this.EventErrorHandler);
            this._userSettingsProtoUpdated = new AsyncEvent<DiscordClient, UserSettingsProtoUpdateEventArgs>("USER_SETTINGS_PROTO_UPDATED", this.EventErrorHandler);
            this._userUpdated = new AsyncEvent<DiscordClient, UserUpdateEventArgs>("USER_UPDATED", this.EventErrorHandler);
            this._voiceStateUpdated = new AsyncEvent<DiscordClient, VoiceStateUpdateEventArgs>("VOICE_STATE_UPDATED", this.EventErrorHandler);
            this._voiceServerUpdated = new AsyncEvent<DiscordClient, VoiceServerUpdateEventArgs>("VOICE_SERVER_UPDATED", this.EventErrorHandler);
            this._guildMembersChunked = new AsyncEvent<DiscordClient, GuildMembersChunkEventArgs>("GUILD_MEMBERS_CHUNKED", this.EventErrorHandler);
            this._unknownEvent = new AsyncEvent<DiscordClient, UnknownEventArgs>("UNKNOWN_EVENT", this.EventErrorHandler);
            this._messageReactionAdded = new AsyncEvent<DiscordClient, MessageReactionAddEventArgs>("MESSAGE_REACTION_ADDED", this.EventErrorHandler);
            this._messageReactionRemoved = new AsyncEvent<DiscordClient, MessageReactionRemoveEventArgs>("MESSAGE_REACTION_REMOVED", this.EventErrorHandler);
            this._messageReactionsCleared = new AsyncEvent<DiscordClient, MessageReactionsClearEventArgs>("MESSAGE_REACTIONS_CLEARED", this.EventErrorHandler);
            this._messageReactionRemovedEmoji = new AsyncEvent<DiscordClient, MessageReactionRemoveEmojiEventArgs>("MESSAGE_REACTION_REMOVED_EMOJI", this.EventErrorHandler);
            this._webhooksUpdated = new AsyncEvent<DiscordClient, WebhooksUpdateEventArgs>("WEBHOOKS_UPDATED", this.EventErrorHandler);
            this._heartbeated = new AsyncEvent<DiscordClient, HeartbeatEventArgs>("HEARTBEATED", this.EventErrorHandler);
            this._zombied = new AsyncEvent<DiscordClient, ZombiedEventArgs>("ZOMBIED", this.EventErrorHandler);
            this._applicationCommandCreated = new AsyncEvent<DiscordClient, ApplicationCommandEventArgs>("APPLICATION_COMMAND_CREATED", this.EventErrorHandler);
            this._applicationCommandUpdated = new AsyncEvent<DiscordClient, ApplicationCommandEventArgs>("APPLICATION_COMMAND_UPDATED", this.EventErrorHandler);
            this._applicationCommandDeleted = new AsyncEvent<DiscordClient, ApplicationCommandEventArgs>("APPLICATION_COMMAND_DELETED", this.EventErrorHandler);
            this._applicationCommandPermissionsUpdated = new AsyncEvent<DiscordClient, ApplicationCommandPermissionsUpdatedEventArgs>("APPLICATION_COMMAND_PERMISSIONS_UPDATED", this.EventErrorHandler);
            this._integrationCreated = new AsyncEvent<DiscordClient, IntegrationCreateEventArgs>("INTEGRATION_CREATED", this.EventErrorHandler);
            this._integrationUpdated = new AsyncEvent<DiscordClient, IntegrationUpdateEventArgs>("INTEGRATION_UPDATED", this.EventErrorHandler);
            this._integrationDeleted = new AsyncEvent<DiscordClient, IntegrationDeleteEventArgs>("INTEGRATION_DELETED", this.EventErrorHandler);
            this._stageInstanceCreated = new AsyncEvent<DiscordClient, StageInstanceCreateEventArgs>("STAGE_INSTANCE_CREATED", this.EventErrorHandler);
            this._stageInstanceUpdated = new AsyncEvent<DiscordClient, StageInstanceUpdateEventArgs>("STAGE_INSTANCE_UPDATED", this.EventErrorHandler);
            this._stageInstanceDeleted = new AsyncEvent<DiscordClient, StageInstanceDeleteEventArgs>("STAGE_INSTANCE_DELETED", this.EventErrorHandler);
            this._relationshipAdded = new AsyncEvent<DiscordClient, RelationshipAddEventArgs>("RELATIONSHIP_ADDED", this.EventErrorHandler);
            this._relationshipRemoved = new AsyncEvent<DiscordClient, RelationshipRemoveEventArgs>("RElATIONSHIP_REMOVED", this.EventErrorHandler);
            this._readStateUpdated = new AsyncEvent<DiscordClient, ReadStateUpdateEventArgs>("READ_STATE_UPDTED", this.EventErrorHandler);
            this._authTokenUpdate = new AsyncEvent<DiscordClient, AuthTokenUpdatedEventArgs>("AUTH_TOKEN_UPDATED", this.EventErrorHandler);
            this._loggedOut = new AsyncEvent<DiscordClient, LoggedOutEventArgs>("LOGGED_OUT", this.EventErrorHandler);

            #region Threads
            this._threadCreated = new AsyncEvent<DiscordClient, ThreadCreateEventArgs>("THREAD_CREATED", this.EventErrorHandler);
            this._threadUpdated = new AsyncEvent<DiscordClient, ThreadUpdateEventArgs>("THREAD_UPDATED", this.EventErrorHandler);
            this._threadDeleted = new AsyncEvent<DiscordClient, ThreadDeleteEventArgs>("THREAD_DELETED", this.EventErrorHandler);
            this._threadListSynced = new AsyncEvent<DiscordClient, ThreadListSyncEventArgs>("THREAD_LIST_SYNCED", this.EventErrorHandler);
            this._threadMemberUpdated = new AsyncEvent<DiscordClient, ThreadMemberUpdateEventArgs>("THREAD_MEMBER_UPDATED", this.EventErrorHandler);
            this._threadMembersUpdated = new AsyncEvent<DiscordClient, ThreadMembersUpdateEventArgs>("THREAD_MEMBERS_UPDATED", this.EventErrorHandler);
            #endregion

            this._guilds.Clear();
            this._presences.Clear();
        }

        #endregion

        #region Client Extension Methods

        /// <summary>
        /// Registers an extension with this client.
        /// </summary>
        /// <param name="ext">Extension to register.</param>
        /// <returns></returns>
        public void AddExtension(BaseExtension ext)
        {
            ext.Setup(this);
            this._extensions.Add(ext);
        }

        /// <summary>
        /// Retrieves a previously-registered extension from this client.
        /// </summary>
        /// <typeparam name="T">Type of extension to retrieve.</typeparam>
        /// <returns>The requested extension.</returns>
        public T GetExtension<T>() where T : BaseExtension
            => this._extensions.FirstOrDefault(x => x.GetType() == typeof(T)) as T;

        #endregion

        #region Public Connection Methods

        /// <summary>
        /// Connects to the gateway
        /// </summary>
        /// <returns></returns>
        /// <exception cref="Exceptions.UnauthorizedException">Thrown when an invalid token was provided.</exception>
        /// <exception cref="Exceptions.BadRequestException">Thrown when an invalid parameter was provided.</exception>
        /// <exception cref="Exceptions.ServerErrorException">Thrown when Discord is unable to process the request.</exception>
        public async Task ConnectAsync(DiscordActivity activity = null, UserStatus? status = null, DateTimeOffset? idlesince = null)
        {
            // Check if connection lock is already set, and set it if it isn't
            if (!this.ConnectionLock.Wait(0))
                throw new InvalidOperationException("This client is already connected.");
            this.ConnectionLock.Set();

            var w = 7500;
            var i = 5;
            var s = false;
            Exception cex = null;

            if (activity == null && status == null && idlesince == null)
                this._status = null;
            else
            {
                var since_unix = idlesince != null ? (long?)Utilities.GetUnixTime(idlesince.Value) : null;
                this._status = new StatusUpdate()
                {
                    Activity = new TransportActivity(activity),
                    Status = status ?? UserStatus.Online,
                    IdleSince = since_unix,
                    IsAFK = idlesince != null,
                    _activity = activity
                };
            }

            if (!this._isShard)
            {
                if (this.Configuration.TokenType != TokenType.Bot)
                    this.Logger.LogWarning(LoggerEvents.Misc, "You are logging in with a token that is not a bot token. This is not officially supported by Discord, and can result in your account being terminated if you aren't careful.");
                this.Logger.LogInformation(LoggerEvents.Startup, "DSharpPlus, version {Version}", this.VersionString);
            }

            while (i-- > 0 || this.Configuration.ReconnectIndefinitely)
            {
                try
                {
                    await this.InternalConnectAsync().ConfigureAwait(false);
                    s = true;
                    break;
                }
                catch (UnauthorizedException e)
                {
                    FailConnection(this.ConnectionLock);
                    throw;
                }
                catch (PlatformNotSupportedException)
                {
                    FailConnection(this.ConnectionLock);
                    throw;
                }
                catch (NotImplementedException)
                {
                    FailConnection(this.ConnectionLock);
                    throw;
                }
                catch (Exception ex)
                {
                    FailConnection(null);

                    cex = ex;
                    if (i <= 0 && !this.Configuration.ReconnectIndefinitely) break;

                    this.Logger.LogError(LoggerEvents.ConnectionFailure, ex, "Connection attempt failed, retrying in {Seconds}s", w / 1000);
                    await Task.Delay(w).ConfigureAwait(false);

                    if (i > 0)
                        w *= 2;
                }
            }

            if (!s && cex != null)
            {
                this.ConnectionLock.Set();
                throw new Exception("Could not connect to Discord.", cex);
            }

            // non-closure, hence args
            static void FailConnection(ManualResetEventSlim cl) =>
                // unlock this (if applicable) so we can let others attempt to connect
                cl?.Set();
        }

        public async Task ReconnectAsync(bool startNewSession = false)
        {
            this.Configuration.AutoReconnect = true;
            await this.InternalReconnectAsync(startNewSession, code: startNewSession ? 1000 : 4002);
        }

        /// <summary>
        /// Disconnects from the gateway
        /// </summary>
        /// <returns></returns>
        public async Task DisconnectAsync(int code = 1000)
        {
            this.Configuration.AutoReconnect = false;
            if (this._webSocketClient != null)
                await this._webSocketClient.DisconnectAsync(code).ConfigureAwait(false);
        }

        #endregion

        #region Public REST Methods

        /// <summary>
        /// Gets a sticker.
        /// </summary>
        /// <param name="stickerId">The ID of the sticker.</param>
        /// <returns>The specified sticker</returns>
        public Task<DiscordMessageSticker> GetStickerAsync(ulong stickerId)
            => this.ApiClient.GetStickerAsync(stickerId);

        /// <summary>
        /// Gets a collection of sticker packs that may be used by nitro users.
        /// </summary>
        /// <returns></returns>
        public Task<IReadOnlyList<DiscordMessageStickerPack>> GetStickerPacksAsync()
            => this.ApiClient.GetStickerPacksAsync();

        public async Task UpdateBannerColorAsync(int color) => await this.ApiClient.ModifyBannerColorAsync(color).ConfigureAwait(false);

        /// <summary>
        /// Updates the protobuf user settings.
        /// </summary>
        /// <param name="base64Proto">Base-64-encoded user settings protobuf.</param>
        /// <returns></returns>
        public async Task UpdateUserSettingsProto(string base64Proto) => await ApiClient.SendSettingsProto(base64Proto).ConfigureAwait(false);

        /// <summary>
        /// Gets a user
        /// </summary>
        /// <param name="userId">ID of the user</param>
        /// <param name="updateCache">Whether to always make a REST request and update cache. Passing true will update the user, updating stale properties such as <see cref="DiscordUser.BannerHash"/>.</param>
        /// <returns></returns>
        /// <exception cref="Exceptions.BadRequestException">Thrown when an invalid parameter was provided.</exception>
        /// <exception cref="Exceptions.ServerErrorException">Thrown when Discord is unable to process the request.</exception>
        [Obsolete("You probably shouldn't call this in Aerochat, as it's an unsupported API for user accounts. Use GetUserProfileAsync and access the .User property of the result instead.")]
        public async Task<DiscordUser> GetUserAsync(ulong userId, bool updateCache = false)
        {
            if (!updateCache && this.TryGetCachedUserInternal(userId, out var usr))
                return usr;

            usr = await this.ApiClient.GetUserAsync(userId).ConfigureAwait(false);

            // See BaseDiscordClient.UpdateUser for why this is done like this.
            this.UserCache.AddOrUpdate(userId, usr, (_, _) => usr);

            return usr;
        }

        /// <summary>
        /// Gets a user profile
        /// </summary>
        /// <param name="userId">ID of the user</param>
        /// <param name="updateCache">Whether to always make a REST request and update cache. Passing true will update the user profile, updating stale properties such as <see cref="DiscordUser.BannerHash"/>.</param>
        /// <returns></returns>
        /// <exception cref="Exceptions.BadRequestException">Thrown when an invalid parameter was provided.</exception>
        /// <exception cref="Exceptions.ServerErrorException">Thrown when Discord is unable to process the request.</exception>
        public async Task<DiscordProfile> GetUserProfileAsync(ulong userId, bool updateCache = false)
        {
            if (!updateCache && this.TryGetCachedUserProfileInternal(userId, out var usr))
                return usr;

            usr = await this.ApiClient.GetUserProfileAsync(userId).ConfigureAwait(false);

            // See BaseDiscordClient.UpdateUser for why this is done like this.
            this.UserProfileCache.AddOrUpdate(userId, usr, (_, _) => usr);

            return usr;
        }

        /// <summary>
        /// Gets a channel
        /// </summary>
        /// <param name="id">The ID of the channel to get.</param>
        /// <returns></returns>
        /// <exception cref="Exceptions.NotFoundException">Thrown when the channel does not exist.</exception>
        /// <exception cref="Exceptions.BadRequestException">Thrown when an invalid parameter was provided.</exception>
        /// <exception cref="Exceptions.ServerErrorException">Thrown when Discord is unable to process the request.</exception>
        public async Task<DiscordChannel> GetChannelAsync(ulong id)
            => this.InternalGetCachedThread(id) ?? this.InternalGetCachedChannel(id) ?? await this.ApiClient.GetChannelAsync(id).ConfigureAwait(false);

        /// <summary>
        /// Sends a message
        /// </summary>
        /// <param name="channel">Channel to send to.</param>
        /// <param name="content">Message content to send.</param>
        /// <returns>The Discord Message that was sent.</returns>
        /// <exception cref="Exceptions.UnauthorizedException">Thrown when the client does not have the <see cref="Permissions.SendMessages"/> permission.</exception>
        /// <exception cref="Exceptions.NotFoundException">Thrown when the channel does not exist.</exception>
        /// <exception cref="Exceptions.BadRequestException">Thrown when an invalid parameter was provided.</exception>
        /// <exception cref="Exceptions.ServerErrorException">Thrown when Discord is unable to process the request.</exception>
        public Task<DiscordMessage> SendMessageAsync(DiscordChannel channel, string content)
            => this.ApiClient.CreateMessageAsync(channel.Id, content, embeds: null, replyMessageId: null, mentionReply: false, failOnInvalidReply: false, suppressNotifications: false);

        /// <summary>
        /// Sends a message
        /// </summary>
        /// <param name="channel">Channel to send to.</param>
        /// <param name="embed">Embed to attach to the message.</param>
        /// <returns>The Discord Message that was sent.</returns>
        /// <exception cref="Exceptions.UnauthorizedException">Thrown when the client does not have the <see cref="Permissions.SendMessages"/> permission.</exception>
        /// <exception cref="Exceptions.NotFoundException">Thrown when the channel does not exist.</exception>
        /// <exception cref="Exceptions.BadRequestException">Thrown when an invalid parameter was provided.</exception>
        /// <exception cref="Exceptions.ServerErrorException">Thrown when Discord is unable to process the request.</exception>
        public Task<DiscordMessage> SendMessageAsync(DiscordChannel channel, DiscordEmbed embed)
            => this.ApiClient.CreateMessageAsync(channel.Id, null, embed != null ? new[] { embed } : null, replyMessageId: null, mentionReply: false, failOnInvalidReply: false, suppressNotifications: false);

        /// <summary>
        /// Sends a message
        /// </summary>
        /// <param name="channel">Channel to send to.</param>
        /// <param name="content">Message content to send.</param>
        /// <param name="embed">Embed to attach to the message.</param>
        /// <returns>The Discord Message that was sent.</returns>
        /// <exception cref="Exceptions.UnauthorizedException">Thrown when the client does not have the <see cref="Permissions.SendMessages"/> permission.</exception>
        /// <exception cref="Exceptions.NotFoundException">Thrown when the channel does not exist.</exception>
        /// <exception cref="Exceptions.BadRequestException">Thrown when an invalid parameter was provided.</exception>
        /// <exception cref="Exceptions.ServerErrorException">Thrown when Discord is unable to process the request.</exception>
        public Task<DiscordMessage> SendMessageAsync(DiscordChannel channel, string content, DiscordEmbed embed)
            => this.ApiClient.CreateMessageAsync(channel.Id, content, embed != null ? new[] { embed } : null, replyMessageId: null, mentionReply: false, failOnInvalidReply: false, suppressNotifications: false);

        /// <summary>
        /// Sends a message
        /// </summary>
        /// <param name="channel">Channel to send to.</param>
        /// <param name="builder">The Discord Message builder.</param>
        /// <returns>The Discord Message that was sent.</returns>
        /// <exception cref="Exceptions.UnauthorizedException">Thrown when the client does not have the <see cref="Permissions.SendMessages"/> permission if TTS is false and <see cref="Permissions.SendTtsMessages"/> if TTS is true.</exception>
        /// <exception cref="Exceptions.NotFoundException">Thrown when the channel does not exist.</exception>
        /// <exception cref="Exceptions.BadRequestException">Thrown when an invalid parameter was provided.</exception>
        /// <exception cref="Exceptions.ServerErrorException">Thrown when Discord is unable to process the request.</exception>
        public Task<DiscordMessage> SendMessageAsync(DiscordChannel channel, DiscordMessageBuilder builder)
            => this.ApiClient.CreateMessageAsync(channel.Id, builder);

        /// <summary>
        /// Sends a message
        /// </summary>
        /// <param name="channel">Channel to send to.</param>
        /// <param name="action">The Discord Message builder.</param>
        /// <returns>The Discord Message that was sent.</returns>
        /// <exception cref="Exceptions.UnauthorizedException">Thrown when the client does not have the <see cref="Permissions.SendMessages"/> permission if TTS is false and <see cref="Permissions.SendTtsMessages"/> if TTS is true.</exception>
        /// <exception cref="Exceptions.NotFoundException">Thrown when the channel does not exist.</exception>
        /// <exception cref="Exceptions.BadRequestException">Thrown when an invalid parameter was provided.</exception>
        /// <exception cref="Exceptions.ServerErrorException">Thrown when Discord is unable to process the request.</exception>
        public Task<DiscordMessage> SendMessageAsync(DiscordChannel channel, Action<DiscordMessageBuilder> action)
        {
            var builder = new DiscordMessageBuilder();
            action(builder);

            return this.ApiClient.CreateMessageAsync(channel.Id, builder);
        }

        /// <summary>
        /// Creates a guild. This requires the bot to be in less than 10 guilds total.
        /// </summary>
        /// <param name="name">Name of the guild.</param>
        /// <param name="region">Voice region of the guild.</param>
        /// <param name="icon">Stream containing the icon for the guild.</param>
        /// <param name="verificationLevel">Verification level for the guild.</param>
        /// <param name="defaultMessageNotifications">Default message notification settings for the guild.</param>
        /// <param name="systemChannelFlags">System channel flags fopr the guild.</param>
        /// <returns>The created guild.</returns>
        /// <exception cref="Exceptions.NotFoundException">Thrown when the channel does not exist.</exception>
        /// <exception cref="Exceptions.BadRequestException">Thrown when an invalid parameter was provided.</exception>
        /// <exception cref="Exceptions.ServerErrorException">Thrown when Discord is unable to process the request.</exception>
        public Task<DiscordGuild> CreateGuildAsync(string name, string region = null, Optional<Stream> icon = default, VerificationLevel? verificationLevel = null,
            DefaultMessageNotifications? defaultMessageNotifications = null,
            SystemChannelFlags? systemChannelFlags = null)
        {
            var iconb64 = Optional.FromNoValue<string>();
            if (icon.HasValue && icon.Value != null)
                using (var imgtool = new ImageTool(icon.Value))
                    iconb64 = imgtool.GetBase64();
            else if (icon.HasValue)
                iconb64 = null;

            return this.ApiClient.CreateGuildAsync(name, region, iconb64, verificationLevel, defaultMessageNotifications, systemChannelFlags);
        }

        /// <summary>
        /// Creates a guild from a template. This requires the bot to be in less than 10 guilds total.
        /// </summary>
        /// <param name="code">The template code.</param>
        /// <param name="name">Name of the guild.</param>
        /// <param name="icon">Stream containing the icon for the guild.</param>
        /// <returns>The created guild.</returns>
        /// <exception cref="Exceptions.BadRequestException">Thrown when an invalid parameter was provided.</exception>
        /// <exception cref="Exceptions.ServerErrorException">Thrown when Discord is unable to process the request.</exception>
        public Task<DiscordGuild> CreateGuildFromTemplateAsync(string code, string name, Optional<Stream> icon = default)
        {
            var iconb64 = Optional.FromNoValue<string>();
            if (icon.HasValue && icon.Value != null)
                using (var imgtool = new ImageTool(icon.Value))
                    iconb64 = imgtool.GetBase64();
            else if (icon.HasValue)
                iconb64 = null;

            return this.ApiClient.CreateGuildFromTemplateAsync(code, name, iconb64);
        }

        /// <summary>
        /// Gets a guild.
        /// <para>Setting <paramref name="withCounts"/> to true will make a REST request.</para>
        /// </summary>
        /// <param name="id">The guild ID to search for.</param>
        /// <param name="withCounts">Whether to include approximate presence and member counts in the returned guild.</param>
        /// <returns>The requested Guild.</returns>
        /// <exception cref="Exceptions.NotFoundException">Thrown when the guild does not exist.</exception>
        /// <exception cref="Exceptions.BadRequestException">Thrown when an invalid parameter was provided.</exception>
        /// <exception cref="Exceptions.ServerErrorException">Thrown when Discord is unable to process the request.</exception>
        public async Task<DiscordGuild> GetGuildAsync(ulong id, bool? withCounts = null)
        {
            if (this._guilds.TryGetValue(id, out var guild) && (!withCounts.HasValue || !withCounts.Value))
                return guild;

            guild = await this.ApiClient.GetGuildAsync(id, withCounts).ConfigureAwait(false);
            var channels = await this.ApiClient.GetGuildChannelsAsync(guild.Id).ConfigureAwait(false);
            foreach (var channel in channels) guild._channels[channel.Id] = channel;

            return guild;
        }

        /// <summary>
        /// Gets a guild preview
        /// </summary>
        /// <param name="id">The guild ID.</param>
        /// <returns></returns>
        /// <exception cref="Exceptions.NotFoundException">Thrown when the guild does not exist.</exception>
        /// <exception cref="Exceptions.BadRequestException">Thrown when an invalid parameter was provided.</exception>
        /// <exception cref="Exceptions.ServerErrorException">Thrown when Discord is unable to process the request.</exception>
        public Task<DiscordGuildPreview> GetGuildPreviewAsync(ulong id)
            => this.ApiClient.GetGuildPreviewAsync(id);

        /// <summary>
        /// Gets an invite.
        /// </summary>
        /// <param name="code">The invite code.</param>
        /// <param name="withCounts">Whether to include presence and total member counts in the returned invite.</param>
        /// <param name="withExpiration">Whether to include the expiration date in the returned invite.</param>
        /// <returns>The requested Invite.</returns>
        /// <exception cref="Exceptions.NotFoundException">Thrown when the invite does not exists.</exception>
        /// <exception cref="Exceptions.BadRequestException">Thrown when an invalid parameter was provided.</exception>
        /// <exception cref="Exceptions.ServerErrorException">Thrown when Discord is unable to process the request.</exception>
        public Task<DiscordInvite> GetInviteByCodeAsync(string code, bool? withCounts = null, bool? withExpiration = null)
            => this.ApiClient.GetInviteAsync(code, withCounts, withExpiration);

        /// <summary>
        /// Gets a list of connections
        /// </summary>
        /// <returns></returns>
        /// <exception cref="Exceptions.BadRequestException">Thrown when an invalid parameter was provided.</exception>
        /// <exception cref="Exceptions.ServerErrorException">Thrown when Discord is unable to process the request.</exception>
        public Task<IReadOnlyList<DiscordConnection>> GetConnectionsAsync()
            => this.ApiClient.GetUsersConnectionsAsync();

        /// <summary>
        /// Gets a webhook
        /// </summary>
        /// <param name="id">The ID of webhook to get.</param>
        /// <returns></returns>
        /// <exception cref="Exceptions.NotFoundException">Thrown when the webhook does not exist.</exception>
        /// <exception cref="Exceptions.BadRequestException">Thrown when an invalid parameter was provided.</exception>
        /// <exception cref="Exceptions.ServerErrorException">Thrown when Discord is unable to process the request.</exception>
        public Task<DiscordWebhook> GetWebhookAsync(ulong id)
            => this.ApiClient.GetWebhookAsync(id);

        /// <summary>
        /// Gets a webhook
        /// </summary>
        /// <param name="id">The ID of webhook to get.</param>
        /// <param name="token"></param>
        /// <returns></returns>
        /// <exception cref="Exceptions.NotFoundException">Thrown when the webhook does not exist.</exception>
        /// <exception cref="Exceptions.BadRequestException">Thrown when an invalid parameter was provided.</exception>
        /// <exception cref="Exceptions.ServerErrorException">Thrown when Discord is unable to process the request.</exception>
        public Task<DiscordWebhook> GetWebhookWithTokenAsync(ulong id, string token)
            => this.ApiClient.GetWebhookWithTokenAsync(id, token);

        /// <summary>
        /// Updates current user's activity and status.
        /// </summary>
        /// <param name="activity">Activity to set.</param>
        /// <param name="userStatus">Status of the user.</param>
        /// <param name="idleSince">Since when is the client performing the specified activity.</param>
        /// <returns></returns>
        public Task UpdateStatusAsync(DiscordActivity activity = null, UserStatus? userStatus = null, DateTimeOffset? idleSince = null)
            => this.InternalUpdateStatusAsync(activity, userStatus, idleSince);

        /// <summary>
        /// Edits current user.
        /// </summary>
        /// <param name="username">New username.</param>
        /// <param name="avatar">New avatar.</param>
        /// <returns></returns>
        /// <exception cref="Exceptions.NotFoundException">Thrown when the user does not exist.</exception>
        /// <exception cref="Exceptions.BadRequestException">Thrown when an invalid parameter was provided.</exception>
        /// <exception cref="Exceptions.ServerErrorException">Thrown when Discord is unable to process the request.</exception>
        public async Task<DiscordUser> UpdateCurrentUserAsync(string username = null, Optional<Stream> avatar = default)
        {
            var av64 = Optional.FromNoValue<string>();
            if (avatar.HasValue && avatar.Value != null)
                using (var imgtool = new ImageTool(avatar.Value))
                    av64 = imgtool.GetBase64();
            else if (avatar.HasValue)
                av64 = null;

            var usr = await this.ApiClient.ModifyCurrentUserAsync(username, av64).ConfigureAwait(false);

            this.CurrentUser.Username = usr.Username;
            this.CurrentUser.Discriminator = usr.Discriminator;
            this.CurrentUser.AvatarHash = usr.AvatarHash;
            return this.CurrentUser;
        }

        /// <summary>
        /// Gets a guild template by the code.
        /// </summary>
        /// <param name="code">The code of the template.</param>
        /// <returns>The guild template for the code.</returns>
        /// <exception cref="Exceptions.BadRequestException">Thrown when an invalid parameter was provided.</exception>
        /// <exception cref="Exceptions.ServerErrorException">Thrown when Discord is unable to process the request.</exception>
        public Task<DiscordGuildTemplate> GetTemplateAsync(string code)
            => this.ApiClient.GetTemplateAsync(code);

        /// <summary>
        /// Gets all the global application commands for this application.
        /// </summary>
        /// <returns>A list of global application commands.</returns>
        public Task<IReadOnlyList<DiscordApplicationCommand>> GetGlobalApplicationCommandsAsync() =>
            this.ApiClient.GetGlobalApplicationCommandsAsync(this.CurrentApplication.Id);

        /// <summary>
        /// Overwrites the existing global application commands. New commands are automatically created and missing commands are automatically deleted.
        /// </summary>
        /// <param name="commands">The list of commands to overwrite with.</param>
        /// <returns>The list of global commands.</returns>
        public Task<IReadOnlyList<DiscordApplicationCommand>> BulkOverwriteGlobalApplicationCommandsAsync(IEnumerable<DiscordApplicationCommand> commands) =>
            this.ApiClient.BulkOverwriteGlobalApplicationCommandsAsync(this.CurrentApplication.Id, commands);

        /// <summary>
        /// Creates or overwrites a global application command.
        /// </summary>
        /// <param name="command">The command to create.</param>
        /// <returns>The created command.</returns>
        public Task<DiscordApplicationCommand> CreateGlobalApplicationCommandAsync(DiscordApplicationCommand command) =>
            this.ApiClient.CreateGlobalApplicationCommandAsync(this.CurrentApplication.Id, command);

        /// <summary>
        /// Gets a global application command by its id.
        /// </summary>
        /// <param name="commandId">The ID of the command to get.</param>
        /// <returns>The command with the ID.</returns>
        public Task<DiscordApplicationCommand> GetGlobalApplicationCommandAsync(ulong commandId) =>
            this.ApiClient.GetGlobalApplicationCommandAsync(this.CurrentApplication.Id, commandId);

        /// <summary>
        /// Gets a global application command by its name.
        /// </summary>
        /// <param name="commandName">The name of the command to get.</param>
        /// <returns>The command with the name.</returns>
        public async Task<DiscordApplicationCommand> GetGlobalApplicationCommandAsync(string commandName)
        {
            foreach (var command in await this.ApiClient.GetGlobalApplicationCommandsAsync(this.CurrentApplication.Id))
                if (command.Name == commandName)
                    return command;

            return null;
        }

        /// <summary>
        /// Edits a global application command.
        /// </summary>
        /// <param name="commandId">The ID of the command to edit.</param>
        /// <param name="action">Action to perform.</param>
        /// <returns>The edited command.</returns>
        public async Task<DiscordApplicationCommand> EditGlobalApplicationCommandAsync(ulong commandId, Action<ApplicationCommandEditModel> action)
        {
            var mdl = new ApplicationCommandEditModel();
            action(mdl);
            var applicationId = this.CurrentApplication?.Id ?? (await this.GetCurrentApplicationAsync().ConfigureAwait(false)).Id;
            return await this.ApiClient.EditGlobalApplicationCommandAsync(applicationId, commandId, mdl.Name, mdl.Description, mdl.Options, mdl.DefaultPermission, default, default, mdl.AllowDMUsage, mdl.DefaultMemberPermissions).ConfigureAwait(false);
        }

        /// <summary>
        /// Deletes a global application command.
        /// </summary>
        /// <param name="commandId">The ID of the command to delete.</param>
        public Task DeleteGlobalApplicationCommandAsync(ulong commandId) =>
            this.ApiClient.DeleteGlobalApplicationCommandAsync(this.CurrentApplication.Id, commandId);

        /// <summary>
        /// Gets all the application commands for a guild.
        /// </summary>
        /// <param name="guildId">The ID of the guild to get application commands for.</param>
        /// <returns>A list of application commands in the guild.</returns>
        public Task<IReadOnlyList<DiscordApplicationCommand>> GetGuildApplicationCommandsAsync(ulong guildId) =>
            this.ApiClient.GetGuildApplicationCommandsAsync(this.CurrentApplication.Id, guildId);

        /// <summary>
        /// Overwrites the existing application commands in a guild. New commands are automatically created and missing commands are automatically deleted.
        /// </summary>
        /// <param name="guildId">The ID of the guild.</param>
        /// <param name="commands">The list of commands to overwrite with.</param>
        /// <returns>The list of guild commands.</returns>
        public Task<IReadOnlyList<DiscordApplicationCommand>> BulkOverwriteGuildApplicationCommandsAsync(ulong guildId, IEnumerable<DiscordApplicationCommand> commands) =>
            this.ApiClient.BulkOverwriteGuildApplicationCommandsAsync(this.CurrentApplication.Id, guildId, commands);

        /// <summary>
        /// Creates or overwrites a guild application command.
        /// </summary>
        /// <param name="guildId">The ID of the guild to create the application command in.</param>
        /// <param name="command">The command to create.</param>
        /// <returns>The created command.</returns>
        public Task<DiscordApplicationCommand> CreateGuildApplicationCommandAsync(ulong guildId, DiscordApplicationCommand command) =>
            this.ApiClient.CreateGuildApplicationCommandAsync(this.CurrentApplication.Id, guildId, command);

        /// <summary>
        /// Gets a application command in a guild by its ID.
        /// </summary>
        /// <param name="guildId">The ID of the guild the application command is in.</param>
        /// <param name="commandId">The ID of the command to get.</param>
        /// <returns>The command with the ID.</returns>
        public Task<DiscordApplicationCommand> GetGuildApplicationCommandAsync(ulong guildId, ulong commandId) =>
             this.ApiClient.GetGuildApplicationCommandAsync(this.CurrentApplication.Id, guildId, commandId);

        /// <summary>
        /// Edits a application command in a guild.
        /// </summary>
        /// <param name="guildId">The ID of the guild the application command is in.</param>
        /// <param name="commandId">The ID of the command to edit.</param>
        /// <param name="action">Action to perform.</param>
        /// <returns>The edited command.</returns>
        public async Task<DiscordApplicationCommand> EditGuildApplicationCommandAsync(ulong guildId, ulong commandId, Action<ApplicationCommandEditModel> action)
        {
            var mdl = new ApplicationCommandEditModel();
            action(mdl);
            var applicationId = this.CurrentApplication?.Id ?? (await this.GetCurrentApplicationAsync().ConfigureAwait(false)).Id;
            return await this.ApiClient.EditGuildApplicationCommandAsync(applicationId, guildId, commandId, mdl.Name, mdl.Description, mdl.Options, mdl.DefaultPermission, default, default, mdl.AllowDMUsage, mdl.DefaultMemberPermissions).ConfigureAwait(false);
        }

        /// <summary>
        /// Deletes a application command in a guild.
        /// </summary>
        /// <param name="guildId">The ID of the guild to delete the application command in.</param>
        /// <param name="commandId">The ID of the command.</param>
        public Task DeleteGuildApplicationCommandAsync(ulong guildId, ulong commandId) =>
            this.ApiClient.DeleteGuildApplicationCommandAsync(this.CurrentApplication.Id, guildId, commandId);

        /// <summary>
        /// Gets a list of messages in which the current user was mentioned recently
        /// </summary>
        /// <param name="limit"></param>
        /// <param name="roles"></param>
        /// <param name="everyone"></param>
        /// <returns></returns>
        public Task<IReadOnlyList<DiscordMessage>> GetMentionsAsync(int limit = 25, bool roles = true, bool everyone = true)
            => this.ApiClient.GetMentionsAsync(limit, roles, everyone);

        /// <summary>
        /// Performs a search in a given guild
        /// </summary>
        public Task<DiscordSearchResult> SearchAsync(DiscordGuild guild,
                                                     string content = null,
                                                     ulong[] authorIds = null,
                                                     ulong[] channelIds = null,
                                                     ulong[] mentionIds = null,
                                                     ulong? min_id = null,
                                                     ulong? max_id = null,
                                                     DiscordSearchFlags hasFlags = DiscordSearchFlags.None,
                                                     int? offset = null)
            => this.ApiClient.SearchGuildAsync(guild.Id, content, authorIds, channelIds, mentionIds, min_id, max_id, hasFlags, offset);

        /// <summary>
        /// Performs a search in a given guild
        /// </summary>
        public Task<DiscordSearchResult> SearchAsync(DiscordChannel channel,
                                                     string content = null,
                                                     ulong[] authorIds = null,
                                                     ulong[] channelIds = null,
                                                     ulong[] mentionIds = null,
                                                     ulong? min_id = null,
                                                     ulong? max_id = null,
                                                     DiscordSearchFlags hasFlags = DiscordSearchFlags.None,
                                                     int? offset = null)
            => this.ApiClient.SearchChannelAsync(channel.Id, content, authorIds, channelIds, mentionIds, min_id, max_id, hasFlags, offset);

        public Task<ThreadQueryResult> SearchForumChannelAsync(DiscordForumChannel channel,
                                                               bool archived,
                                                               bool ascending,
                                                               int limit,
                                                               int offset,
                                                               ulong[] tagIds)
            => this.ApiClient.SearchForumChannelAsync(channel.Id, channel.GuildId.Value, archived, ascending, limit, offset, tagIds);

        public Task<DiscordForumDataResult> GetForumPostDataAsync(DiscordForumChannel channel, ulong[] thread_ids)
            => this.ApiClient.GetForumPostDataAsync(channel.Id, channel.GuildId.Value, thread_ids);

        #endregion

        #region Public WebSocket Methods

        /// <summary>
        /// Requests guild sync for specified guilds. Guild sync sends information about members and presences for a given guild, and makes gateway dispatch additional events.
        /// 
        /// This can only be done for user tokens.
        /// </summary>
        /// <param name="guilds">Guilds to send a sync request for.</param>
        public async Task SyncGuildsAsync(params DiscordGuild[] guilds)
        {
            if (this.Configuration.TokenType != TokenType.User)
            {
                throw new InvalidOperationException("This can only be done for user tokens.");
            }

            var to_sync = guilds.Where(xg => !xg._isSynced);
            foreach (var guild in to_sync)
            {
                var guild_sync = new GatewayPayload
                {
                    OpCode = GatewayOpCode.LazyRequest,
                    Data = new JObject()
                    {
                        ["guild_id"] = new JValue(guild.Id.ToString()),
                        ["typing"] = new JValue(true),
                        ["activities"] = new JValue(true),
                        ["threads"] = new JValue(true)
                    }
                };

                guild._isSynced = true;

                // TOOD: track most accessed channels for quick stuff
                var guild_syncstr = JsonConvert.SerializeObject(guild_sync);
                await _webSocketClient.SendMessageAsync(guild_syncstr);
            }
        }


        internal async Task RequestUserPresencesAsync(DiscordGuild discordGuild, IEnumerable<ulong> usersToSync)
        {
            var request = new GatewayPayload
            {
                OpCode = GatewayOpCode.RequestGuildMembers,
                Data = new JObject()
                {
                    ["guild_id"] = new JArray() { discordGuild.Id.ToString() },
                    ["user_ids"] = new JArray(usersToSync)
                }
            };

            this.Logger.LogDebug($"Requesting {usersToSync.Count()} members");

            var guild_syncstr = JsonConvert.SerializeObject(request);
            await _webSocketClient.SendMessageAsync(guild_syncstr);
        }

        public async Task UpdateVoiceStateAsync(ulong? guildId, ulong? channelId, bool selfMute, bool selfDeaf)
        {
            var vs = new GatewayPayload
            {
                OpCode = GatewayOpCode.VoiceStateUpdate,
                Data = new JObject()
                {
                    ["guild_id"] = guildId.ToString(),
                    ["channel_id"] = channelId?.ToString(),
                    ["self_mute"] = selfMute,
                    ["self_deaf"] = selfDeaf
                }
            };
            
            var vsstr = JsonConvert.SerializeObject(vs);
            await _webSocketClient.SendMessageAsync(vsstr);
        }

        #endregion

        #region Public Cache Methods
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryGetCachedGuild(ulong id, out DiscordGuild guild)
            => (guild = this.InternalGetCachedGuild(id)) != null;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryGetCachedChannel(ulong id, out DiscordChannel channel)
            => (channel = this.InternalGetCachedChannel(id)) != null;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryGetCachedUser(ulong id, out DiscordUser user)
            => this.TryGetCachedUserInternal(id, out user);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryGetCachedThread(ulong id, out DiscordThreadChannel thread)
            => (thread = this.InternalGetCachedThread(id)) != null;
        #endregion

        #region DANGER ZONE
        //[MethodImpl(MethodImplOptions.AggressiveInlining)]
        //public Task SendSocketMessageAsync(string message)
        //    => _webSocketClient.SendMessageAsync(message);
        #endregion

        #region Internal Caching Methods

        internal DiscordThreadChannel InternalGetCachedThread(ulong threadId)
        {
            foreach (var guild in this.Guilds)
                if (guild.Value.Threads.TryGetValue(threadId, out var foundThread))
                    return foundThread;

            return null;
        }

        internal DiscordChannel InternalGetCachedChannel(ulong channelId)
        {
            var foundDmChannel = default(DiscordDmChannel);
            if (this._privateChannels?.TryGetValue(channelId, out foundDmChannel) == true)
                return foundDmChannel;

            foreach (var guild in this.Guilds)
                if (guild.Value.Channels.TryGetValue(channelId, out var foundChannel))
                    return foundChannel;

            return this.InternalGetCachedThread(channelId); // this may break things but might also make things crash less
        }

        internal DiscordGuild InternalGetCachedGuild(ulong? guildId)
        {
            if (this._guilds != null && guildId.HasValue)
            {
                if (this._guilds.TryGetValue(guildId.Value, out var guild))
                    return guild;
            }

            return null;
        }

        private void UpdateMessage(DiscordMessage message, TransportUser author, DiscordGuild guild, TransportMember member)
        {
            if (author != null)
            {
                var usr = new DiscordUser(author) { Discord = this };

                if (member != null)
                    member.User = author;

                message.Author = this.UpdateUser(usr, guild?.Id, guild, member);
            }

            var channel = this.InternalGetCachedChannel(message.ChannelId) ?? this.InternalGetCachedThread(message.ChannelId);

            if (channel != null) return;

            channel = !message._guildId.HasValue
                ? new DiscordDmChannel
                {
                    Id = message.ChannelId,
                    Discord = this,
                    Type = ChannelType.Private,
                    Recipients = new DiscordUser[] { message.Author }
                }
                : new DiscordChannel
                {
                    Id = message.ChannelId,
                    GuildId = guild?.Id,
                    Discord = this
                };

            message.Channel = channel;
        }

        private DiscordUser UpdateUser(DiscordUser usr, ulong? guildId, DiscordGuild guild, TransportMember mbr)
        {
            if (mbr != null)
            {
                if (mbr.User != null)
                {
                    usr = new DiscordUser(mbr.User) { Discord = this };

                    this.UpdateUserCache(usr);

                    usr = new DiscordMember(mbr) { Discord = this, _guild_id = guildId.Value };
                }

                var intents = this.Configuration.Intents;

                DiscordMember member = default;

                if (!intents.HasAllPrivilegedIntents() || guild.IsLarge) // we have the necessary privileged intents, no need to worry about caching here unless guild is large.
                {
                    if (guild?._members.TryGetValue(usr.Id, out member) == false)
                    {
                        if (intents.HasIntent(DiscordIntents.GuildMembers) || this.Configuration.AlwaysCacheMembers) // member can be updated by events, so cache it
                        {
                            guild._members.TryAdd(usr.Id, (DiscordMember)usr);
                        }
                    }
                    else if (intents.HasIntent(DiscordIntents.GuildPresences) || this.Configuration.AlwaysCacheMembers) // we can attempt to update it if it's already in cache.
                    {
                        if (!intents.HasIntent(DiscordIntents.GuildMembers)) // no need to update if we already have the member events
                        {
                            _ = guild._members.TryUpdate(usr.Id, (DiscordMember)usr, member);
                        }
                    }
                }
            }
            else if (usr.Username != null) // check if not a skeleton user
            {
                this.UpdateUserCache(usr);
            }

            return usr;
        }

        private void UpdateCachedGuild(DiscordGuild newGuild, JArray rawMembers)
        {
            if (this._disposed)
                return;

            if (!this._guilds.ContainsKey(newGuild.Id))
                this._guilds[newGuild.Id] = newGuild;

            var guild = this._guilds[newGuild.Id];

            if (newGuild._channels != null && newGuild._channels.Count > 0)
            {
                foreach (var channel in newGuild._channels.Values)
                {
                    if (guild._channels.TryGetValue(channel.Id, out _)) continue;

                    foreach (var overwrite in channel._permissionOverwrites)
                    {
                        overwrite.Discord = this;
                        overwrite._channel_id = channel.Id;
                    }

                    guild._channels[channel.Id] = channel;
                }
            }
            if (newGuild._threads != null && newGuild._threads.Count > 0)
            {
                foreach (var thread in newGuild._threads.Values)
                {
                    if (guild._threads.TryGetValue(thread.Id, out _)) continue;

                    guild._threads[thread.Id] = thread;
                }
            }

            foreach (var newEmoji in newGuild._emojis.Values)
                _ = guild._emojis.GetOrAdd(newEmoji.Id, _ => newEmoji);

            foreach (var newSticker in newGuild._stickers.Values)
                _ = guild._stickers.GetOrAdd(newSticker.Id, _ => newSticker);

            if (rawMembers != null)
            {
                guild._members.Clear();

                foreach (var xj in rawMembers)
                {
                    var xtm = xj.ToDiscordObject<TransportMember>();

                    var xu = new DiscordUser(xtm.User) { Discord = this };
                    this.UpdateUserCache(xu);

                    guild._members[xtm.User.Id] = new DiscordMember(xtm) { Discord = this, _guild_id = guild.Id };
                }
            }

            foreach (var role in newGuild._roles.Values)
            {
                if (guild._roles.TryGetValue(role.Id, out _)) continue;

                role._guild_id = guild.Id;
                guild._roles[role.Id] = role;
            }

            if (newGuild._stageInstances != null)
                foreach (var newStageInstance in newGuild._stageInstances.Values)
                    _ = guild._stageInstances.GetOrAdd(newStageInstance.Id, _ => newStageInstance);

            guild.Name = newGuild.Name;
            guild._afkChannelId = newGuild._afkChannelId;
            guild.AfkTimeout = newGuild.AfkTimeout;
            guild.DefaultMessageNotifications = newGuild.DefaultMessageNotifications;
            guild.Features = newGuild.Features;
            guild.IconHash = newGuild.IconHash;
            guild.MfaLevel = newGuild.MfaLevel;
            guild.OwnerId = newGuild.OwnerId;
            guild._voiceRegionId = newGuild._voiceRegionId;
            guild.SplashHash = newGuild.SplashHash;
            guild.VerificationLevel = newGuild.VerificationLevel;
            guild.WidgetEnabled = newGuild.WidgetEnabled;
            guild._widgetChannelId = newGuild._widgetChannelId;
            guild.ExplicitContentFilter = newGuild.ExplicitContentFilter;
            guild.PremiumTier = newGuild.PremiumTier;
            guild.PremiumSubscriptionCount = newGuild.PremiumSubscriptionCount;
            guild.Banner = newGuild.Banner;
            guild.Description = newGuild.Description;
            guild.VanityUrlCode = newGuild.VanityUrlCode;
            guild.Banner = newGuild.Banner;
            guild._systemChannelId = newGuild._systemChannelId;
            guild.SystemChannelFlags = newGuild.SystemChannelFlags;
            guild.DiscoverySplashHash = newGuild.DiscoverySplashHash;
            guild.MaxMembers = newGuild.MaxMembers;
            guild.MaxPresences = newGuild.MaxPresences;
            guild.ApproximateMemberCount = newGuild.ApproximateMemberCount;
            guild.ApproximatePresenceCount = newGuild.ApproximatePresenceCount;
            guild.MaxVideoChannelUsers = newGuild.MaxVideoChannelUsers;
            guild.PreferredLocale = newGuild.PreferredLocale;
            guild._rulesChannelId = newGuild._rulesChannelId;
            guild._publicUpdatesChannelId = newGuild._publicUpdatesChannelId;
            guild.PremiumProgressBarEnabled = newGuild.PremiumProgressBarEnabled;

            // fields not sent for update:
            // - guild.Channels
            // - voice states
            // - guild.JoinedAt = new_guild.JoinedAt;
            // - guild.Large = new_guild.Large;
            // - guild.MemberCount = Math.Max(new_guild.MemberCount, guild._members.Count);
            // - guild.Unavailable = new_guild.Unavailable;
        }

        private void PopulateMessageReactionsAndCache(DiscordMessage message, TransportUser author, TransportMember member)
        {
            var guild = message.Channel?.Guild ?? this.InternalGetCachedGuild(message._guildId);

            this.UpdateMessage(message, author, guild, member);

            if (message._reactions == null)
                message._reactions = new List<DiscordReaction>();
            foreach (var xr in message._reactions)
                xr.Emoji.Discord = this;

            if (this.Configuration.MessageCacheSize > 0 && message.Channel != null)
                this.MessageCache?.Add(message);
        }


        #endregion

        #region Disposal

        private bool _disposed;

        /// <summary>
        /// Disposes your DiscordClient.
        /// </summary>
        public override void Dispose()
        {
            if (this._disposed)
                return;

            this._disposed = true;

            this.DisconnectAsync().GetAwaiter().GetResult();
            this.ApiClient?._rest?.Dispose();
            this.CurrentUser = null!;

            var extensions = this._extensions; // prevent _extensions being modified during dispose
            this._extensions = null!;

            foreach (var extension in extensions)
            {
                if (extension is IDisposable disposable)
                    disposable.Dispose();
            }

            try
            {
                this._cancelTokenSource?.Cancel();
                this._cancelTokenSource?.Dispose();
            }
            catch { }

            this._guilds = null!;
            this._heartbeatTask = null!;
            this._privateChannels = null!;
        }

        #endregion
    }
}
