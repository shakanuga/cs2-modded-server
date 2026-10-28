using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using System.Text.Json;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;

namespace CourseFix;

// Workarounds für alte Course-Maps, die aus CS:GO portiert wurden. Wird nur im
// Course-Modus geladen (course.cfg) und bei jedem Moduswechsel wieder entladen
// (unload_plugins.cfg).
//
// 1. Zu wenige Spawnpunkte: CS2 lässt nur so viele Spieler in ein Team, wie die Map
//    Spawnpunkte dafür hat. Danach heißt es "Team voll", obwohl Slots frei sind.
//    -> Fehlende Spawnpunkte werden als Kopien der vorhandenen ergänzt.
// 2. Todeszone am Start: Viele dieser Maps schalten nach einer festen Zeit eine
//    Schadenszone (trigger_hurt) am Spawn scharf, damit die Runde endet. Mit
//    Respawn im Course-Modus stirbt man dann in Dauerschleife.
//    -> Erst wenn jemand kurz nach dem Spawn durch die Map stirbt, werden
//       Schadenszonen, die einen Spawnpunkt berühren, bis zum Mapwechsel
//       abgeschaltet. Vorher bleiben sie aktiv, damit z. B. eine große
//       Todeszone unter der Map normal funktioniert.
// 3. !r ohne Startzone: SharpTimer braucht für !r eine Startzone, die diese Maps
//    nicht haben ("No RespawnPos found"). -> Dann bringt !r dich an einen
//    Spawnpunkt deines Teams zurück.
public class CourseFix : BasePlugin
{
    public override string ModuleName => "CourseFix";
    public override string ModuleVersion => "1.2.0";
    public override string ModuleAuthor => "shakanuga";
    public override string ModuleDescription => "Extra spawn points and no death zones at spawn for course maps";

    private const int MinSpawnsPerTeam = 32;
    private static readonly string[] SpawnNames = { "info_player_counterterrorist", "info_player_terrorist" };

    // Positionen der Spawnpunkte (Kopien, damit keine Entity-Referenzen veralten).
    private readonly List<(float X, float Y, float Z)> _spawns = new();
    private readonly HashSet<uint> _reported = new();

    // Sekunden nach dem Spawn, in denen ein Tod durch die Map als Spawn-Tod zählt.
    private const float SpawnDeathWindow = 4.0f;
    private readonly Dictionary<int, float> _spawnTime = new();
    private bool _spawnDeathSeen = false;

    // Spawnpunkte je Team mit Blickrichtung, für !r.
    private readonly Dictionary<CsTeam, List<(float X, float Y, float Z, float Yaw)>> _teamSpawns = new();
    private bool _hasStartZone = true;
    private static readonly string[] StartTriggerNames = { "map_start", "s1_start", "stage1_start", "timer_startzone", "zone_start" };
    private static readonly string[] RespawnChat = { "!r", "/r", ".r", "!restart", "/restart" };
    private readonly Random _random = new();

    public override void Load(bool hotReload)
    {
        RegisterListener<Listeners.OnMapStart>(_ =>
        {
            _spawns.Clear();
            _reported.Clear();
            _spawnTime.Clear();
            _spawnDeathSeen = false;
            AddTimer(2.0f, Setup, TimerFlags.STOP_ON_MAPCHANGE);
        });

        RegisterEventHandler<EventRoundStart>((_, _) =>
        {
            AddTimer(0.5f, Setup, TimerFlags.STOP_ON_MAPCHANGE);
            return HookResult.Continue;
        });

        RegisterEventHandler<EventPlayerSpawn>((e, _) =>
        {
            if (e.Userid is { IsValid: true } p)
                _spawnTime[p.Slot] = Server.CurrentTime;
            return HookResult.Continue;
        });

        RegisterEventHandler<EventPlayerDeath>((e, _) =>
        {
            var victim = e.Userid;
            if (victim == null || !victim.IsValid || _spawnDeathSeen)
                return HookResult.Continue;

            // Tod durch die Map (kein Spieler als Angreifer) kurz nach dem Spawn.
            bool byWorld = e.Attacker == null || !e.Attacker.IsValid || e.Attacker == victim;
            if (byWorld && _spawnTime.TryGetValue(victim.Slot, out var t) && Server.CurrentTime - t < SpawnDeathWindow)
            {
                _spawnDeathSeen = true;
                Log($"{victim.PlayerName} ist {Server.CurrentTime - t:0.0}s nach dem Spawn gestorben - Schadenszonen am Spawn werden ab jetzt abgeschaltet.");
                DisableSpawnHurts();
            }
            return HookResult.Continue;
        });

        AddTimer(1.0f, DisableSpawnHurts, TimerFlags.REPEAT);

        // !r abfangen, bevor SharpTimer "No RespawnPos found" meldet.
        AddCommandListener("css_r", (player, _) => TeleportToStart(player) ? HookResult.Handled : HookResult.Continue, HookMode.Pre);
        AddCommandListener("say", OnSay, HookMode.Pre);
        AddCommandListener("say_team", OnSay, HookMode.Pre);

        // Auch beim ersten Laden mitten auf der Map sofort greifen.
        AddTimer(1.0f, Setup);
    }

    private void Setup()
    {
        var byTeam = SpawnNames.ToDictionary(
            n => n,
            n => Utilities.FindAllEntitiesByDesignerName<SpawnPoint>(n)
                .Where(s => s.IsValid && s.AbsOrigin != null)
                .ToList());

        _spawns.Clear();
        foreach (var s in byTeam.Values.SelectMany(l => l))
            _spawns.Add((s.AbsOrigin!.X, s.AbsOrigin.Y, s.AbsOrigin.Z));

        _teamSpawns.Clear();
        _teamSpawns[CsTeam.CounterTerrorist] = byTeam["info_player_counterterrorist"].Select(ToSpawn).ToList();
        _teamSpawns[CsTeam.Terrorist] = byTeam["info_player_terrorist"].Select(ToSpawn).ToList();

        bool hadStart = _hasStartZone;
        _hasStartZone = MapHasStartZone();
        if (!_hasStartZone && hadStart)
            Log("Keine SharpTimer-Startzone auf dieser Map - !r bringt Spieler zum Spawn zurück.");

        if (_spawns.Count == 0)
        {
            Log("Map hat keine Spawnpunkte, nichts zu tun.");
            return;
        }

        foreach (var name in SpawnNames)
        {
            var own = byTeam[name];
            // Hat ein Team gar keine Spawns, die des anderen Teams als Vorlage nehmen.
            var templates = own.Count > 0 ? own : byTeam.Where(kv => kv.Key != name).SelectMany(kv => kv.Value).ToList();
            if (templates.Count == 0 || own.Count >= MinSpawnsPerTeam)
                continue;

            int created = 0;
            for (int i = own.Count; i < MinSpawnsPerTeam; i++)
            {
                var src = templates[i % templates.Count];
                var sp = Utilities.CreateEntityByName<SpawnPoint>(name);
                if (sp == null)
                    break;
                sp.Teleport(
                    new Vector(src.AbsOrigin!.X, src.AbsOrigin.Y, src.AbsOrigin.Z),
                    src.AbsRotation != null ? new QAngle(src.AbsRotation.X, src.AbsRotation.Y, src.AbsRotation.Z) : new QAngle(0, 0, 0),
                    new Vector(0, 0, 0));
                sp.DispatchSpawn();
                created++;
            }
            if (created > 0)
                Log($"{name}: {own.Count} Spawnpunkte vorhanden, {created} ergänzt.");
        }

        DisableSpawnHurts();
    }

    private void DisableSpawnHurts()
    {
        if (_spawns.Count == 0 || !_spawnDeathSeen)
            return;

        foreach (var hurt in Utilities.FindAllEntitiesByDesignerName<CTriggerHurt>("trigger_hurt"))
        {
            if (!hurt.IsValid || hurt.Disabled || hurt.Damage <= 0 || hurt.AbsOrigin == null)
                continue;

            var o = hurt.AbsOrigin;
            var mins = hurt.Collision.Mins;
            var maxs = hurt.Collision.Maxs;
            float minX = o.X + mins.X, minY = o.Y + mins.Y, minZ = o.Z + mins.Z;
            float maxX = o.X + maxs.X, maxY = o.Y + maxs.Y, maxZ = o.Z + maxs.Z;

            // Spieler-Hitbox am Spawn: ca. 32x32 Einheiten breit, 72 hoch.
            bool touchesSpawn = _spawns.Any(p =>
                p.X + 16 >= minX && p.X - 16 <= maxX &&
                p.Y + 16 >= minY && p.Y - 16 <= maxY &&
                p.Z + 72 >= minZ && p.Z - 8 <= maxZ);

            if (!touchesSpawn)
                continue;

            hurt.AcceptInput("Disable");
            if (_reported.Add(hurt.Index))
                Log($"Schadenszone am Spawn abgeschaltet: trigger_hurt #{hurt.Index} '{hurt.Entity?.Name}' ({hurt.Damage} Schaden)");
        }
    }

    private static (float X, float Y, float Z, float Yaw) ToSpawn(SpawnPoint s) =>
        (s.AbsOrigin!.X, s.AbsOrigin.Y, s.AbsOrigin.Z, s.AbsRotation?.Y ?? 0);

    // Hat SharpTimer für diese Map eine Startposition? Entweder über einen
    // Start-Trigger in der Map oder über RespawnPos in cfg/SharpTimer/MapData/<map>.json.
    private static bool MapHasStartZone()
    {
        foreach (var t in Utilities.FindAllEntitiesByDesignerName<CBaseTrigger>("trigger_multiple"))
        {
            var name = t.Entity?.Name;
            if (!string.IsNullOrEmpty(name) && StartTriggerNames.Contains(name))
                return true;
        }

        try
        {
            var path = Path.Join(Server.GameDirectory, "csgo", "cfg", "SharpTimer", "MapData", $"{Server.MapName}.json");
            if (File.Exists(path))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                if (doc.RootElement.TryGetProperty("RespawnPos", out var pos) && !string.IsNullOrWhiteSpace(pos.GetString()))
                    return true;
            }
        }
        catch
        {
            // Kaputte JSON: so tun, als gäbe es keine Startposition.
        }
        return false;
    }

    private HookResult OnSay(CCSPlayerController? player, CommandInfo info)
    {
        var msg = info.GetArg(1).Trim();
        if (RespawnChat.Contains(msg, StringComparer.OrdinalIgnoreCase) && TeleportToStart(player))
            return HookResult.Handled;
        return HookResult.Continue;
    }

    // true = erledigt (SharpTimer soll nicht mehr ran), false = SharpTimer macht es selbst.
    private bool TeleportToStart(CCSPlayerController? player)
    {
        if (_hasStartZone || player == null || !player.IsValid || player.IsBot)
            return false;

        if (!player.PawnIsAlive)
        {
            player.Respawn();
            return true;
        }

        if (!_teamSpawns.TryGetValue(player.Team, out var list) || list.Count == 0)
            list = _teamSpawns.Values.SelectMany(l => l).ToList();
        var pawn = player.PlayerPawn.Value;
        if (list.Count == 0 || pawn == null)
            return false;

        var sp = list[_random.Next(list.Count)];
        pawn.Teleport(new Vector(sp.X, sp.Y, sp.Z), new QAngle(0, sp.Yaw, 0), new Vector(0, 0, 0));
        player.PrintToChat($" {ChatColors.Green}[CourseFix]{ChatColors.Default} Zurück zum Start.");
        return true;
    }

    private void Log(string msg) => Server.PrintToConsole($"[CourseFix] {msg}");
}
