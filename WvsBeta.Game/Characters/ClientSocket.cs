﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using Bert.RateLimiters;
using log4net;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using WvsBeta.Common;
using WvsBeta.Common.Sessions;
using WvsBeta.Common.Tracking;
using WvsBeta.Game.GameObjects;
using WvsBeta.Game.GameObjects.MiniRooms;
using WvsBeta.Game.Handlers;
using WvsBeta.Game.Packets;

namespace WvsBeta.Game
{
    public class ClientSocket : AbstractConnection
    {
        private static ILog log = LogManager.GetLogger("ClientSocket");
        public Player Player { get; }
        public bool Loaded { get; set; }


        private readonly IThrottleStrategy _throttleStrategy = new FixedTokenBucket(50, 1, 1000); // 50 incoming packets allowed per second
        private readonly IThrottleStrategy _throttleWarningStrategy = new FixedTokenBucket(1, 1, 2000); // warn max once per 2 seconds

        private static readonly ClientMessages[] ExcludedFromThrottle =
        {
            ClientMessages.MOB_MOVE, // Excluded in case a ton of mobs get spawned, and not really feasible to exploit
            ClientMessages.MOB_APPLY_CONTROL, // Buggy client implementation causes this to be sent a lot
        };

        private static PacketTimingTracker<ClientMessages> ptt = new PacketTimingTracker<ClientMessages>();

        static ClientSocket()
        {
            MasterThread.RepeatingAction.Start("PacketTimingTracker Flush ClientSocket", ptt.Flush, 0, 60 * 1000);
        }

        public ClientSocket(Socket pSocket, IPEndPoint endPoint)
            : base(pSocket)
        {
            SetIPEndPoint(endPoint);
            Loaded = false;

            Player = new Player
            {
                Socket = this,
                Character = null
            };
            Server.Instance.AddPlayer(Player);
            Pinger.Add(this);

            SendHandshake(Constants.MAPLE_VERSION, Constants.MAPLE_PATCH_LOCATION, Constants.MAPLE_LOCALE);
            SendMemoryRegions();
        }


        public override void StartLogging()
        {
            base.StartLogging();
            Player?.Character?.SetupLogging();
        }

        public override void EndLogging()
        {
            base.EndLogging();
            Character.RemoveLogging();
        }

        public override void OnDisconnect()
        {
            try
            {
                StartLogging();

                if (Loaded)
                {
                    Player.Character?.Destroy(Player.IsCC);
                    Loaded = false;
                }

                Player.Socket = null;
                Server.Instance.RemovePlayer(Player.SessionHash);
            }
            catch (Exception ex)
            {
                Program.MainForm.LogAppend(ex.ToString());
            }
            finally
            {
                EndLogging();
                Pinger.Remove(this);
            }
        }

        private static readonly HashSet<ClientMessages> logPackets = new HashSet<ClientMessages>
        {
            ClientMessages.MINI_ROOM_OPERATION, ClientMessages.STORAGE_ACTION, ClientMessages.SHOP_ACTION
        };

        public override void AC_OnPacketInbound(Packet packet)
        {
            ptt.StartMeasurement();

            var header = (ClientMessages) packet.ReadByte();
            try
            {
                if (!ExcludedFromThrottle.Contains(header) && _throttleStrategy.ShouldThrottle())
                {
                    if (!_throttleWarningStrategy.ShouldThrottle())
                    {
                        var throttleWarning = $"Packet {header} hit throttle limit from ";

                        var chr = Player?.Character;
                        if (chr == null)
                        {
                            throttleWarning += IP;
                        }
                        else
                        {
                            throttleWarning += chr.Name + " on map " + chr.MapID;
                        }

                        Server.Instance.ServerTraceDiscordReporter.Enqueue(throttleWarning);
                        Program.MainForm.LogAppend(throttleWarning);
                    }

                    // Uncomment to drop packets on throttle. currently not enabled to keep an eye on warnings
                    // return;
                }

                if (!Loaded || Player?.Character == null)
                {
                    switch (header)
                    {
                        case ClientMessages.MIGRATE_IN:
                            OnPlayerLoad(packet);
                            break; //updated
                    }
                }
                // Block packets as we are migrating
                else if (Server.Instance.InMigration == false || Server.Instance.IsNewServerInMigration)
                {
                    var character = Player.Character;

                    if (logPackets.Contains(header))
                        PacketLog.ReceivedPacket(packet, (byte) header, Server.Instance.Name, IP);

                    switch (header)
                    {
                        case ClientMessages.ENTER_PORTAL:
                            MapPacket.OnEnterPortal(packet, character);
                            break;
                        case ClientMessages.ENTER_SCRIPTED_PORTAL:
                            MapPacket.OnEnterScriptedPortal(packet, character);
                            break;
                        case ClientMessages.CHANGE_CHANNEL:
                            OnChangeChannel(character, packet);
                            break;
                        case ClientMessages.ENTER_CASH_SHOP:
                            OnEnterCashShop(character);
                            break;
                        case ClientMessages.MOVE_PLAYER:
                            MapPacket.HandleMove(character, packet);
                            break;
                        case ClientMessages.SIT_REQUEST:
                            MapPacket.HandleSitChair(character, packet);
                            break;
                        case ClientMessages.ENTER_TOWN_PORTAL:
                            MapPacket.HandleDoorUse(character, packet);
                            break;
                        case ClientMessages.CLOSE_RANGE_ATTACK:
                            AttackPacket.HandleMeleeAttack(character, packet);
                            break;
                        case ClientMessages.RANGED_ATTACK:
                            AttackPacket.HandleRangedAttack(character, packet);
                            break;
                        case ClientMessages.MAGIC_ATTACK:
                            AttackPacket.HandleMagicAttack(character, packet);
                            break;
                        case ClientMessages.TAKE_DAMAGE:
                            CharacterStatsPacket.HandleCharacterDamage(character, packet);
                            break;

                        case ClientMessages.CHAT:
                            MessagePacket.HandleChat(character, packet);
                            break;
                        case ClientMessages.GROUP_MESSAGE:
                            MessagePacket.HandleSpecialChat(character, packet);
                            break;
                        case ClientMessages.WHISPER:
                            MessagePacket.HandleCommand(character, packet);
                            break;
                        case ClientMessages.EMOTE:
                            MapPacket.SendEmotion(character, packet.ReadInt());
                            break;

                        case ClientMessages.NPC_TALK:
                            NpcPacket.HandleStartNpcChat(character, packet);
                            break;
                        case ClientMessages.NPC_TALK_MORE:
                            NpcPacket.HandleNPCChat(character, packet);
                            break;
                        case ClientMessages.SHOP_ACTION:
                            NpcPacket.HandleNPCShop(character, packet);
                            break;
                        case ClientMessages.STORAGE_ACTION:
                            StoragePacket.HandleStorage(character, packet);
                            break;

                        case ClientMessages.ITEM_MOVE:
                            InventoryPacket.HandleInventoryPacket(character, packet);
                            break;
                        case ClientMessages.ITEM_USE:
                            InventoryPacket.HandleUseItemPacket(character, packet);
                            break;
                        case ClientMessages.SUMMON_BAG_USE:
                            InventoryPacket.HandleUseSummonSack(character, packet);
                            break;
                        case ClientMessages.PET_EAT_FOOD:
                            PetsPacket.HandlePetFood(character, packet);
                            break;
                        case ClientMessages.CASH_ITEM_USE:
                            CashPacket.HandleCashItem(character, packet);
                            break;
                        case ClientMessages.RETURN_SCROLL_USE:
                            InventoryPacket.HandleUseReturnScroll(character, packet);
                            break;
                        case ClientMessages.SCROLL_USE:
                            InventoryPacket.HandleScrollItem(character, packet);
                            break;

                        case ClientMessages.DISTRIBUTE_AP:
                            CharacterStatsPacket.HandleStats(character, packet);
                            break;
                        case ClientMessages.HEAL_OVER_TIME:
                            CharacterStatsPacket.HandleHeal(character, packet);
                            break;
                        case ClientMessages.DISTRIBUTE_SP:
                            SkillPacket.HandleAddSkillLevel(character, packet);
                            break;
                        case ClientMessages.PREPARE_SKILL:
                            SkillPacket.HandlePrepareSkill(character, packet);
                            break;
                        case ClientMessages.GIVE_BUFF:
                            SkillPacket.HandleUseSkill(character, packet);
                            break;
                        case ClientMessages.CANCEL_BUFF:
                            SkillPacket.HandleStopSkill(character, packet);
                            break;

                        case ClientMessages.DROP_MESOS:
                            DropPacket.HandleDropMesos(character, packet.ReadInt());
                            break;
                        case ClientMessages.GIVE_FAME:
                            FamePacket.HandleFame(character, packet);
                            break;
                        case ClientMessages.CHAR_INFO_REQUEST:
                            MapPacket.SendPlayerInfo(character, packet);
                            break;
                        case ClientMessages.SPAWN_PET:
                            PetsPacket.HandleSpawnPet(character, packet.ReadShort());
                            break;

                        case ClientMessages.SUMMON_MOVE:
                            MapPacket.HandleSummonMove(character, packet);
                            break;

                        case ClientMessages.SUMMON_ATTACK:
                            AttackPacket.HandleSummonAttack(character, packet);
                            break;

                        case ClientMessages.SUMMON_DAMAGED:
                            MapPacket.HandleSummonDamage(character, packet);
                            break;

                        case ClientMessages.MOB_MOVE:
                            MobPacket.HandleMobControl(character, packet);
                            break;
                        case ClientMessages.MOB_APPLY_CONTROL:
                            MobPacket.HandleApplyControl(character, packet);
                            break;

                        case ClientMessages.NPC_ANIMATE:
                            NpcPacket.HandleNPCAnimation(character, packet);
                            break;

                        case ClientMessages.PET_MOVE:
                            PetsPacket.HandleMovePet(character, packet);
                            break;
                        case ClientMessages.PET_INTERACTION:
                            PetsPacket.HandleInteraction(character, packet);
                            break;
                        case ClientMessages.PET_ACTION:
                            PetsPacket.HandlePetAction(character, packet);
                            break;
                        case ClientMessages.PET_LOOT:
                            PetsPacket.HandlePetLoot(character, packet);
                            break;

                        case ClientMessages.FIELD_CONTIMOVE_STATE:
                            MapPacket.OnContiMoveState(character, packet);
                            break;

                        case ClientMessages.DROP_PICK_UP:
                            DropPacket.HandlePickupDrop(character, packet);
                            break;

                        case ClientMessages.MESSENGER:
                            MessengerHandler.HandleMessenger(character, packet);
                            break;

                        case ClientMessages.MEMO_OPERATION:
                            MemoPacket.OnPacket(character, packet);
                            break;

                        case ClientMessages.MINI_ROOM_OPERATION:
                            MiniRoomPacket.HandlePacket(character, packet);
                            break;
                        case ClientMessages.FRIEND_OPERATION:
                            BuddyHandler.HandleBuddy(character, packet);
                            break;
                        case ClientMessages.PARTY_OPERATION:
                            PartyHandler.HandleParty(character, packet);
                            break;

                        case ClientMessages.DENY_PARTY_REQUEST:
                            PartyHandler.HandleDecline(character, packet);
                            break;

                        case ClientMessages.REACTOR_HIT:
                            ReactorPacket.HandleReactorHit(character, packet);
                            break;

                        case ClientMessages.REPORT_USER:
                            MiscPacket.ReportPlayer(character, packet);
                            break;
                        //this is a garbage opcode that i use when doing janky client packet workarounds. This is where packets go to die.
                        case ClientMessages.JUNK:
                            Program.MainForm.LogDebug("received junk packet");
                            break;

                        // eh.. ignore?
                        // Happens when one of the following buffs are set:
                        // Stun, Poison, Seal, Darkness, Weakness, Curse
                        // Maybe patch out of the client
                        case ClientMessages.CHARACTER_IS_DEBUFFED: break;

                        case ClientMessages.CLIENT_HASH: break;

                        case ClientMessages.CFG:
                            OnCfgPacket(character, packet);
                            break;

                        case ClientMessages.PONG: OnPong(character); break;

                        default:
                            if (character.Field.HandlePacket(character, packet, header) == false)
                            {
                                Program.MainForm.LogAppend(
                                    "[{0}] Unknown packet received! " + packet,
                                    header
                                );
                            }

                            break;
                    }
                }
            }
            catch (Exception ex)
            {
                Program.MainForm.LogAppend($"---- ERROR ----\r\n{ex}");
                Program.MainForm.LogAppend($"Packet: {packet}");
                FileWriter.WriteLine(@"etclog\ExceptionCatcher.log", "[Game Server " + Server.Instance.ID + "][" + DateTime.Now + "] Exception caught: " + ex, true);
                //Disconnect();
            }


            if (Player?.Character != null && Player.Character.ExclRequestSet)
            {
                // Need to unset it.
                log.Debug($"Having to send NoChange packet to unlock player. Packet {header}");
                InventoryPacket.NoChange(Player.Character);
            }

#if DEBUG
            if (packet.Length != packet.Position)
            {
                var packetStr = packet.ToString();
                packetStr = packetStr.Substring(0, packet.Position * 3 - 1) + "-x-" + packetStr.Substring(packet.Position * 3);

                log.Debug($"Did not read full message in packet: {header} {packetStr}");
            }
#endif
            ptt.EndMeasurement((byte) header);
        }

        public void OnCfgPacket(Character character, Packet packet)
        {
            switch (packet.ReadByte<CfgClientMessages>())
            {
                case CfgClientMessages.CFG_MEMORY_EDIT_DETECTED: break;

                case CfgClientMessages.CFG_COMMUNICATOR:
                    CommunicatorHandler.OnPacket(character, packet);
                    break;
                case CfgClientMessages.CFG_GUILD:
                    GuildHandler.HandlePacket(character, packet);
                    break;
            }
        }

        public void OnPong(Character character)
        {
            var time = MasterThread.CurrentTime;
            if (time - character.LastPingPacket < 10000) return;
            character.LastPingPacket = time;

            // Make sure we update the player online thing
            RedisBackend.Instance.SetPlayerOnline(
                character.UserID,
                Server.Instance.GetOnlineId()
            );

            // Cleanup expired items
            character.Inventory.CheckExpired();
            character.CheckPetDead();
            character.UpdateActivePet(time);

            // Send memos at first ping. Client should be loaded at this point
            if (!character.MemosSent)
            {
                character.MemosSent = true;
                MemoPacket.SendMemos(character);
            }
        }

        public override void OnHackDetected(List<MemoryEdit> memEdits)
        {
            if (!Loaded || !HackDetected.HasValue) return;
            var character = Player.Character;
            var hack = HackDetected.Value;
            if (hack.HasFlag(RedisBackend.HackKind.Speedhack))
            {
                MessagePacket.SendNoticeGMs(
                    $"Detected speed hacks on character '{character.Name}', map {character.MapID}...",
                    MessagePacket.MessageTypes.RedText);

                character.PermaBan(
                    "Detected speedhack",
                    extraDelay: (int) ((2 * 60) + Rand32.Next() % (5 * 60)),
                    doNotBanForNow: character.IsGM
                );
            }

            if (hack.HasFlag(RedisBackend.HackKind.MemoryEdits))
            {
                var formattedText = $"Detected memory edits on character '{character.Name}', map {character.MapID}";
                var memRegionsText = "";
                if (memEdits?.Count > 0)
                {
                    memRegionsText += "\n```Address  | Changed\n";
                    foreach (var memoryEdit in memEdits)
                    {
                        memRegionsText += $"{memoryEdit.address} | {memoryEdit.aob}\n";
                    }

                    memRegionsText += "```";
                }

                MessagePacket.SendNoticeGMs(
                    formattedText,
                    MessagePacket.MessageTypes.RedText
                );

                // Add some randomness
                character.PermaBan(
                    "Detected memory edits" + memRegionsText,
                    // Between 2 and 12 minutes
                    extraDelay: (int) ((2 * 60) + Rand32.Next() % (10 * 60)),
                    doNotBanForNow: character.IsGM || Server.Instance.MemoryAutobanEnabled == false
                );
            }
        }

        public void OnChangeChannel(Character character, Packet packet)
        {
            if (character.Field.DisableChangeChannel)
            {
                MapPacket.BlockedMessage(character, MapPacket.PortalBlockedMessage.CannotGoToThatPlace);
                return;
            }

            var channel = packet.ReadByte();
            DoChangeChannelReq(channel);
        }

        public void OnEnterCashShop(Character character)
        {
            if (character.Field.DisableGoToCashShop)
            {
                MapPacket.BlockedMessage(character, MapPacket.PortalBlockedMessage.CannotGoToThatPlace);
                return;
            }
            
            Server.Instance.CenterConnection.RequestCharacterConnectToWorld(
                Player.SessionHash,
                character.ID,
                Server.Instance.WorldID,
                50,
                character
            );
        }

        public void DoChangeChannelReq(byte channel)
        {
            Server.Instance.CenterConnection.RequestCharacterConnectToWorld(
                Player.SessionHash,
                Player.Character.ID,
                Server.Instance.WorldID,
                channel,
                Player.Character
            );
        }

        public void SendConnectToServer(byte[] ipAddr, ushort port, bool noScheduledDisconnect = false)
        {
            if (port != 0 && IP == "127.0.0.1")
            {
                // Use local address for local connections
                ipAddr = new byte[] { 127, 0, 0, 1 };
            }
            
            log.Info($"Connecting to {ipAddr[0]}.{ipAddr[1]}.{ipAddr[2]}.{ipAddr[3]}:{port}");

            var pw = new Packet(ServerMessages.CHANGE_CHANNEL);
            pw.WriteBool(true);
            pw.WriteBytes(ipAddr);
            pw.WriteUShort(port);
            SendPacket(pw);

            if (!noScheduledDisconnect)
            {
                ScheduleDisconnect();
            }
        }

        public void OnPlayerLoad(Packet packet)
        {
            var characterId = packet.ReadInt();
            var ccToken = packet.ReadBytes(16);
            var lang = packet.ReadString();
            var activeCodePage = packet.ReadInt();
            ThreadContext.Properties["CharacterID"] = characterId;

            if (RedisBackend.Instance.HoldoffPlayerConnection(characterId))
            {
                Program.MainForm.LogAppend("Bouncing charid: " + characterId);
                SendConnectToServer(Server.Instance.PublicIP.GetAddressBytes(), Server.Instance.Port, true);
                return;
            }

            if (RedisBackend.Instance.PlayerIsMigrating(characterId, true) == false)
            {
                var msg = "Disconnecting because not migrating. Charid: " + characterId;
                Server.Instance.ServerTraceDiscordReporter.Enqueue(msg);
                Program.MainForm.LogAppend(msg);
                goto cleanup_and_disconnect;
            }

            if (Server.Instance.CharacterList.TryGetValue(characterId, out var huskCharacter) && !huskCharacter.HuskMode)
            {
                var msg = $"Disconnecting characterId {characterId} from IP {IP}. Already connected in this channel.";
                Server.Instance.ServerTraceDiscordReporter.Enqueue(msg);
                Program.MainForm.LogAppend(msg);
                goto cleanup_and_disconnect;
            }

            var uId = Server.Instance.CharacterDatabase.UserIDByCharID(characterId);
            ThreadContext.Properties["UserID"] = uId;
            if (Server.Instance.CharacterDatabase.IsBanned(uId))
            {
                var msg = $"Disconnecting because banned. Charid: {characterId}. Userid: {uId}";
                Server.Instance.ServerTraceDiscordReporter.Enqueue(msg);
                Program.MainForm.LogAppend(msg);
                goto cleanup_and_disconnect;
            }

            if (Server.Instance.CharacterDatabase.IsIpBanned(IP))
            {
                var msg = $"Disconnecting because IP banned. Charid: {characterId}. Userid: {uId}. IP: {IP}";
                Server.Instance.ServerTraceDiscordReporter.Enqueue(msg);
                Program.MainForm.LogAppend(msg);
                goto cleanup_and_disconnect;
            }

            if (RedisBackend.Instance.RunningNormally &&
                !ccToken.SequenceEqual(RedisBackend.Instance.GetCCToken(characterId)))
            {
                var msg = $"Disconnecting because CC token mismatched. Charid: {characterId}. Userid: {uId}. IP: {IP}";
                Server.Instance.ServerTraceDiscordReporter.Enqueue(msg);
                Program.MainForm.LogAppend(msg);
                goto cleanup_and_disconnect;
            }

            Character character;
            if (huskCharacter != null)
            {
                character = huskCharacter;
                log.Info("Using husk as character....");
                character.LeaveHuskMode();
                character.InvokeForcedReturnOnShopExit = true;
            }
            else
            {
                character = new Character(characterId);
                var loadResult = character.Load(IP);
                if (loadResult != Character.LoadFailReasons.None)
                {
                    var msg = $"Disconnected characterId {characterId} from IP {IP}. {loadResult}";
                    Server.Instance.ServerTraceDiscordReporter.Enqueue(msg);
                    Program.MainForm.LogAppend(msg);
                    goto cleanup_and_disconnect;
                }
            }

            character.ClientActiveCodePage = activeCodePage;
            character.ClientUILanguage = lang;

            Player.Character = character;
            character.Player = Player;

            StartLogging();

            Program.MainForm.LogAppend($"{character.Name} connected from IP {IP}, lang {lang}, codepage {activeCodePage}.");

            if (huskCharacter == null)
            {
                Program.MainForm.ChangeLoad(true);
            }

            Server.Instance.CharacterList[characterId] = character;
            if (character.IsGM)
                Server.Instance.StaffCharacters.Add(character);

            Loaded = true;

            //have to load summons after he joins the map, so i have moved this from the load method -Exile
            if (Server.Instance.CCIngPlayerList.TryGetValue(character.ID, out var info))
            {
                Server.Instance.CCIngPlayerList.Remove(character.ID);
            }

            var ccPacket = info?.Item1;

            if (ccPacket != null)
            {
                character.PrimaryStats.DecodeForCC(ccPacket);
            }

            character.PrimaryStats.CheckHPMP(true);
            
            var location = huskCharacter?.Position;
            if (location != null)
            {
                character.Position.X = location.X;
                character.Position.Y = location.Y;
                character.ForcedLocation = true;
            }

            MapPacket.SendJoinGame(character);

            if (character.IsGM)
            {
                var glevel = character.GMLevel switch
                {
                    1 => "(GM Intern)",
                    2 => "(GM)",
                    _ => "(Admin)"
                };
                MessagePacket.SendNotice(character, $"Your GM Level: {character.GMLevel} {glevel}. Undercover? {(character.Undercover ? "Yes" : "No")}");
            }

            
            character.TryHideOnMapEnter();

            
            character.Field.AddPlayer(character);

            if (ccPacket != null)
            {
                character.Summons.DecodeForCC(ccPacket);
            }

            MessagePacket.SendText(MessagePacket.MessageTypes.Header, Server.Instance.ScrollingHeader, character, MessagePacket.MessageMode.ToPlayer);

            Server.Instance.CenterConnection.RegisterCharacter(character);

            character.IsOnline = true;
            Server.Instance.CenterConnection.PlayerUpdateMap(character);
            character.MonsterBook.SendUpdate();

            var guild = character.Guild;
            if (guild != null)
            {
                guild.UpdatePlayer(character);
                guild.SendGuildInfoUpdate(character);
            }

            // Just to be sure, check if he was banned

            if (RedisBackend.Instance.TryGetNonGameHackDetect(Player.Character.UserID, out var hax))
            {
                HackDetected = hax;
                OnHackDetected(null);
            }
            else if (HackDetected.HasValue)
                OnHackDetected(null);

            huskCharacter?.RoomV2?.ResumeFromHuskMode(huskCharacter);

            SendPacket(CommunicatorHandler.WriteCommunicationData(lang, activeCodePage));

            character.RateCredits.SendUpdate(true);

            return;


            cleanup_and_disconnect:

            Server.Instance.CCIngPlayerList.Remove(characterId);
            Disconnect();
        }
    }
}