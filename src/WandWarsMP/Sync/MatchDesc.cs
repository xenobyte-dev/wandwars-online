using System;
using System.Collections.Generic;
using System.Linq;
using InControl;
using WandWars;
using WandWarsMP.Net;

namespace WandWarsMP.Sync
{
    /// <summary>
    /// Everything a client needs to construct the same match the host just created: mode, level,
    /// config, and the player list (character, skin, team, who controls each one). Serialized in MatchStart.
    /// </summary>
    public sealed class MatchDesc
    {
        public const byte OwnerBot = 255;

        public sealed class PlayerDesc
        {
            public string Character;    // Character.ShortName
            public int Skin = -1;       // index into Character.Skins, -1 none, -2 Christmas
            public byte Number;         // PlayerNumber
            public int Team = -1;       // index into GlobalResources.Teams
            public bool IsBot;
            public byte Owner;          // net slot controlling it (0 = host), OwnerBot for bots
            public int BotDifficulty;
            public List<string> Loadout = new List<string>();
        }

        public MatchType Type;
        public string Level;
        public int ScoreLimit;
        public BallSpeed BallSpeed;
        public int BotDifficulty;
        public int SpellStormDifficulty;
        public List<string> ConfigLoadout;
        public int Seed;
        public readonly List<PlayerDesc> Players = new List<PlayerDesc>();

        /// <summary>Host: describe a match. ownerOf maps a player's input device to a net slot.</summary>
        public static MatchDesc FromMatch(string level, List<Player> players, MatchConfig config, Func<InputDevice, byte> ownerOf, int seed)
        {
            var d = new MatchDesc
            {
                Type = config.MatchType,
                Level = level ?? "",
                ScoreLimit = config.ScoreLimit,
                BallSpeed = config.BallSpeed,
                BotDifficulty = config.BotDifficulty,
                SpellStormDifficulty = config.SpellStormDifficulty,
                ConfigLoadout = config.Loadout?.Items,
                Seed = seed,
            };
            var teams = Game.GlobalResources.Teams;
            foreach (var p in players)
            {
                var c = p.Character;
                int skin = -1;
                if (p.CharacterSkin != null && c != null)
                {
                    if (p.CharacterSkin == c.ChristmasSkin) skin = -2;
                    else if (c.Skins != null) skin = Array.IndexOf(c.Skins, p.CharacterSkin);
                }
                d.Players.Add(new PlayerDesc
                {
                    Character = c != null ? c.ShortName : "",
                    Skin = skin,
                    Number = (byte)p.PlayerNumber,
                    Team = p.Team != null ? teams.IndexOf(p.Team) : -1,
                    IsBot = p.IsBot,
                    Owner = p.IsBot ? OwnerBot : ownerOf(p.InputDevice),
                    BotDifficulty = p.BotDifficulty,
                    Loadout = new List<string>(p.Loadout),
                });
            }
            return d;
        }

        public void Write(NetWriter w)
        {
            w.U8((byte)Type);
            w.Str(Level);
            w.I32(ScoreLimit);
            w.U8((byte)BallSpeed);
            w.I32(BotDifficulty);
            w.I32(SpellStormDifficulty);
            WriteList(w, ConfigLoadout);
            w.I32(Seed);
            w.U8((byte)Players.Count);
            foreach (var p in Players)
            {
                w.Str(p.Character);
                w.I16((short)p.Skin);
                w.U8(p.Number);
                w.I16((short)p.Team);
                w.Bool(p.IsBot);
                w.U8(p.Owner);
                w.I32(p.BotDifficulty);
                WriteList(w, p.Loadout);
            }
        }

        public static MatchDesc Read(ref NetReader r)
        {
            var d = new MatchDesc
            {
                Type = (MatchType)r.U8(),
                Level = r.Str(),
                ScoreLimit = r.I32(),
                BallSpeed = (BallSpeed)r.U8(),
                BotDifficulty = r.I32(),
                SpellStormDifficulty = r.I32(),
                ConfigLoadout = ReadList(ref r),
                Seed = r.I32(),
            };
            int n = r.U8();
            for (int i = 0; i < n; i++)
            {
                d.Players.Add(new PlayerDesc
                {
                    Character = r.Str(),
                    Skin = r.I16(),
                    Number = r.U8(),
                    Team = r.I16(),
                    IsBot = r.Bool(),
                    Owner = r.U8(),
                    BotDifficulty = r.I32(),
                    Loadout = ReadList(ref r) ?? new List<string>(),
                });
            }
            return d;
        }

        private static void WriteList(NetWriter w, List<string> list)
        {
            if (list == null) { w.VarU(0); return; }
            w.VarU((uint)list.Count + 1);
            foreach (var s in list) w.Str(s);
        }

        private static List<string> ReadList(ref NetReader r)
        {
            uint n = r.VarU();
            if (n == 0) return null;
            var list = new List<string>((int)n - 1);
            for (int i = 0; i < n - 1; i++) list.Add(r.Str());
            return list;
        }

        public MatchConfig ToConfig() => new MatchConfig
        {
            MatchType = Type,
            ScoreLimit = ScoreLimit,
            BallSpeed = BallSpeed,
            BotDifficulty = BotDifficulty,
            SpellStormDifficulty = SpellStormDifficulty,
            Loadout = ConfigLoadout == null ? null : new Loadout { Items = new List<string>(ConfigLoadout) },
        };

        /// <summary>Client: build Player objects. Every player gets a puppet device (never attached to
        /// InControl) so the game doesn't grab local pads; entities are then driven from the network.</summary>
        public List<Player> BuildPlayers(Func<PlayerDesc, InputDevice> deviceFor)
        {
            var teams = Game.GlobalResources.Teams;
            var list = new List<Player>();
            foreach (var p in Players)
            {
                Character c;
                Game.GlobalResources.Characters.TryGetValue(p.Character, out c);
                Team team = p.Team >= 0 && p.Team < teams.Count ? teams[p.Team] : null;
                var player = new Player(c, (PlayerNumber)p.Number, deviceFor(p), team, p.IsBot)
                {
                    BotDifficulty = p.BotDifficulty,
                };
                if (c != null)
                {
                    if (p.Skin == -2) player.CharacterSkin = c.ChristmasSkin;
                    else if (p.Skin >= 0 && c.Skins != null && p.Skin < c.Skins.Length) player.CharacterSkin = c.Skins[p.Skin];
                }
                player.Loadout.AddRange(p.Loadout);
                list.Add(player);
            }
            return list;
        }
    }
}
