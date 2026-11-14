using System;
using System.Collections.Generic;
using Terraria;
using Terraria.ID;
using TerrariaApi.Server;
using TShockAPI;
using Microsoft.Xna.Framework;

namespace AutoPlatformPlugin
{
    [ApiVersion(2, 1)]
    public class AutoPlatform : TerrariaPlugin
    {
        public override string Name => "AutoPlatform";
        public override Version Version => new Version(1, 1, 0);
        public override string Author => "CustomPlugin";
        public override string Description => "Memasang platform otomatis 8 arah (horizontal, vertikal, diagonal) sesuai pickaxe power.";

        private class PlayerPlatConfig
        {
            public bool Enabled { get; set; } = false;
            public int Length { get; set; } = 15;
            public bool BreakBlocks { get; set; } = false;
        }

        private Dictionary<int, PlayerPlatConfig> playerConfigs = new Dictionary<int, PlayerPlatConfig>();
        private HashSet<int> processingPlayers = new HashSet<int>();
        private int lastConsumedItemType = 0;

        public AutoPlatform(Main game) : base(game) { }

        public override void Initialize()
        {
            GetDataHandlers.TileEdit += OnTileEdit;
            ServerApi.Hooks.ServerLeave.Register(this, OnServerLeave);
            Commands.ChatCommands.Add(new Command("autoplat.use", AutoPlatCommand, "autoplat", "ap")
            {
                HelpText = "Gunakan /ap [panjang | break | unbreak | off]"
            });
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                GetDataHandlers.TileEdit -= OnTileEdit;
                ServerApi.Hooks.ServerLeave.Deregister(this, OnServerLeave);
            }
            base.Dispose(disposing);
        }

        private void OnServerLeave(LeaveEventArgs args)
        {
            playerConfigs.Remove(args.Who);
            processingPlayers.Remove(args.Who);
        }

        private void AutoPlatCommand(CommandArgs args)
        {
            int index = args.Player.Index;
            if (!playerConfigs.TryGetValue(index, out PlayerPlatConfig config))
            {
                config = new PlayerPlatConfig();
                playerConfigs[index] = config;
            }

            if (args.Parameters.Count == 0)
            {
                config.Enabled = !config.Enabled;
                if (config.Enabled)
                {
                    string modeText = config.BreakBlocks ? "BREAK (Tembus Blok)" : "UNBREAK (Stop di Blok)";
                    args.Player.SendSuccessMessage($"[AutoPlat] Aktif! Panjang: {config.Length} | Mode: {modeText}. Taruh 1 platform ke arah yang diinginkan (8 arah)!");
                }
                else
                {
                    args.Player.SendInfoMessage("[AutoPlat] Dimatikan.");
                }
                return;
            }

            string param = args.Parameters[0].ToLower();

            if (param == "off" || param == "stop" || param == "matikan")
            {
                config.Enabled = false;
                args.Player.SendInfoMessage("[AutoPlat] Dimatikan.");
            }
            else if (param == "break")
            {
                config.Enabled = true;
                config.BreakBlocks = true;
                args.Player.SendSuccessMessage($"[AutoPlat] Mode BREAK aktif (Panjang: {config.Length}). Blok akan ditembus sesuai Pickaxe Power!");
            }
            else if (param == "unbreak")
            {
                config.Enabled = true;
                config.BreakBlocks = false;
                args.Player.SendSuccessMessage($"[AutoPlat] Mode UNBREAK aktif (Panjang: {config.Length}). Platform akan berhenti jika menyentuh blok.");
            }
            else if (int.TryParse(param, out int newLength))
            {
                if (newLength < 2 || newLength > 200)
                {
                    args.Player.SendErrorMessage("[AutoPlat] Panjang platform harus antara 2 sampai 200 blok!");
                    return;
                }

                config.Enabled = true;
                config.Length = newLength;
                string modeText = config.BreakBlocks ? "BREAK" : "UNBREAK";
                args.Player.SendSuccessMessage($"[AutoPlat] Aktif! Panjang diatur ke {config.Length} blok (Mode: {modeText}).");
            }
            else
            {
                args.Player.SendErrorMessage("Format salah! Gunakan: /ap, /ap <jumlah>, /ap break, /ap unbreak, atau /ap off");
            }
        }

        private void OnTileEdit(object sender, GetDataHandlers.TileEditEventArgs args)
        {
            if (args.Action != GetDataHandlers.EditAction.PlaceTile)
                return;

            TSPlayer tsPlayer = args.Player;
            if (tsPlayer == null || processingPlayers.Contains(tsPlayer.Index))
                return;

            if (!playerConfigs.TryGetValue(tsPlayer.Index, out PlayerPlatConfig config) || !config.Enabled)
                return;

            int startX = args.X;
            int startY = args.Y;
            ushort placedTileType = args.EditData;
            byte style = args.Style;

            if (placedTileType != TileID.Platforms && placedTileType != TileID.PlanterBox)
                return;

            Player player = tsPlayer.TPlayer;

            Vector2 playerCenterTile = player.Center / 16f;
            Vector2 delta = new Vector2(startX + 0.5f - playerCenterTile.X, startY + 0.5f - playerCenterTile.Y);

            int dirX = 0;
            int dirY = 0;

            if (delta.LengthSquared() > 0.01f)
            {
                double angle = Math.Atan2(delta.Y, delta.X);
                int sector = (int)Math.Round(angle / (Math.PI / 4.0));
                switch (sector)
                {
                    case 0:  dirX = 1;  dirY = 0;  break;
                    case 1:  dirX = 1;  dirY = 1;  break;
                    case 2:  dirX = 0;  dirY = 1;  break;
                    case 3:  dirX = -1; dirY = 1;  break;
                    case 4:
                    case -4: dirX = -1; dirY = 0;  break;
                    case -3: dirX = -1; dirY = -1; break;
                    case -2: dirX = 0;  dirY = -1; break;
                    case -1: dirX = 1;  dirY = -1; break;
                }
            }
            else
            {
                dirX = player.direction;
            }

            int maxPickPower = GetHighestPickaxePower(player);

            processingPlayers.Add(tsPlayer.Index);
            try
            {
                for (int step = 1; step < config.Length; step++)
                {
                    int targetX = startX + (dirX * step);
                    int targetY = startY + (dirY * step);

                    if (!WorldGen.InWorld(targetX, targetY, 10))
                        break;

                    if (!ConsumeMatchingPlatformItem(tsPlayer, placedTileType, style))
                    {
                        tsPlayer.SendInfoMessage("[AutoPlat] Stok platform di tas habis!");
                        break;
                    }

                    ITile tile = Main.tile[targetX, targetY];
                    if (tile != null && tile.active())
                    {
                        if (Main.tileCut[tile.type])
                        {
                            WorldGen.KillTile(targetX, targetY, false, false, false);
                        }
                        else if (config.BreakBlocks)
                        {
                            if (CanPickaxeBreakTile(targetX, targetY, tile.type, maxPickPower))
                            {
                                WorldGen.KillTile(targetX, targetY, false, false, false);
                            }
                            else
                            {
                                RefundPlatformItem(tsPlayer);
                                break;
                            }
                        }
                        else
                        {
                            RefundPlatformItem(tsPlayer);
                            break;
                        }
                    }

                    bool placed = WorldGen.PlaceTile(targetX, targetY, placedTileType, true, true, tsPlayer.Index, style);
                    if (!placed)
                    {
                        RefundPlatformItem(tsPlayer);
                        break;
                    }

                    if (dirX != 0 && dirY != 0 && placedTileType == TileID.Platforms)
                    {
                        byte slope = (dirX == dirY) ? (byte)1 : (byte)2;
                        WorldGen.SlopeTile(targetX, targetY, slope);
                    }

                    TSPlayer.All.SendTileSquareCentered(targetX, targetY, 1);
                }
            }
            finally
            {
                processingPlayers.Remove(tsPlayer.Index);
            }
        }

        private int GetHighestPickaxePower(Player player)
        {
            int highest = 0;
            for (int i = 0; i < 58; i++)
            {
                Item item = player.inventory[i];
                if (item != null && item.stack > 0 && item.pick > highest)
                {
                    highest = item.pick;
                }
            }
            return highest;
        }

        private bool CanPickaxeBreakTile(int x, int y, ushort tileType, int pickPower)
        {
            if (Main.tileContainer[tileType] || tileType == TileID.DemonAltar || tileType == TileID.LihzahrdAltar)
                return false;

            int requiredPower = 0;

            if (tileType == TileID.Ebonstone || tileType == TileID.Crimstone || tileType == TileID.Pearlstone ||
                tileType == TileID.Hellstone)
            {
                requiredPower = 65;
            }
            else if (tileType == TileID.Meteorite)
            {
                requiredPower = 50;
            }
            else if (tileType == TileID.BlueDungeonBrick || tileType == TileID.GreenDungeonBrick || tileType == TileID.PinkDungeonBrick)
            {
                if (y > Main.worldSurface) requiredPower = 65;
            }
            else if (tileType == TileID.Obsidian)
            {
                requiredPower = 55;
            }
            else if (tileType == TileID.Cobalt || tileType == TileID.Palladium)
            {
                requiredPower = 100;
            }
            else if (tileType == TileID.Mythril || tileType == TileID.Orichalcum)
            {
                requiredPower = 110;
            }
            else if (tileType == TileID.Adamantite || tileType == TileID.Titanium)
            {
                requiredPower = 150;
            }
            else if (tileType == TileID.Chlorophyte)
            {
                requiredPower = 200;
            }
            else if (tileType == TileID.LihzahrdBrick)
            {
                requiredPower = 210;
            }

            return pickPower >= requiredPower;
        }

        private bool ConsumeMatchingPlatformItem(TSPlayer tsPlayer, ushort tileType, byte style)
        {
            Player player = tsPlayer.TPlayer;

            Item held = player.HeldItem;
            if (held != null && held.stack > 0 && held.createTile == tileType && held.placeStyle == style)
            {
                lastConsumedItemType = held.type;
                held.stack--;
                if (held.stack <= 0) held.TurnToAir();
                NetMessage.SendData((int)PacketTypes.PlayerSlot, -1, -1, null, tsPlayer.Index, player.selectedItem);
                return true;
            }

            for (int i = 0; i < 50; i++)
            {
                Item invItem = player.inventory[i];
                if (invItem != null && invItem.stack > 0 && invItem.createTile == tileType && invItem.placeStyle == style)
                {
                    lastConsumedItemType = invItem.type;
                    invItem.stack--;
                    if (invItem.stack <= 0) invItem.TurnToAir();
                    NetMessage.SendData((int)PacketTypes.PlayerSlot, -1, -1, null, tsPlayer.Index, i);
                    return true;
                }
            }

            return false;
        }

        private void RefundPlatformItem(TSPlayer tsPlayer)
        {
            if (lastConsumedItemType > 0)
            {
                tsPlayer.GiveItem(lastConsumedItemType, 1);
            }
        }
    }
}