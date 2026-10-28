using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
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
//    nicht haben ("No RespawnPos found"). -> CourseFix trägt den ersten CT-Spawn
//    als RespawnPos in cfg/SharpTimer/MapData/<map>.json ein. SharpTimer liest die
//    Datei bei jedem Rundenstart, danach funktioniert sein eigenes !r.
public class CourseFix : BasePlugin
{
    public override string ModuleName => "CourseFix";
    public override string ModuleVersion => "1.3.0";
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

    private static readonly string[] StartTriggerNames = { "map_start", "s1_start", "stage1_start", "timer_startzone", "zone_start" };

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

        if (!MapHasStartZone())
        {
            var first = byTeam["info_player_counterterrorist"].FirstOrDefault()
                        ?? byTeam["info_player_terrorist"].FirstOrDefault();
            if (first != null)
                WriteRespawnPos(first.AbsOrigin!.X, first.AbsOrigin.Y, first.AbsOrigin.Z);
        }

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

    private static string MapDataPath() =>
        Path.Join(Server.GameDirectory, "csgo", "cfg", "SharpTimer", "MapData", $"{Server.MapName}.json");

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
            var path = MapDataPath();
            if (File.Exists(path))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                if (doc.RootElement.TryGetProperty("RespawnPos", out var pos) &&
                    pos.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(pos.GetString()))
                    return true;
            }
        }
        catch
        {
            // Kaputte JSON: wird unten neu geschrieben.
        }
        return false;
    }

    // RespawnPos in die MapData-Datei von SharpTimer schreiben, vorhandene Felder bleiben.
    private void WriteRespawnPos(float x, float y, float z)
    {
        try
        {
            var path = MapDataPath();
            JsonObject obj = new();
            if (File.Exists(path))
            {
                try { obj = JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? new(); }
                catch { obj = new(); }
            }

            // Etwas über dem Boden, damit man nicht im Boden steckt.
            obj["RespawnPos"] = string.Create(CultureInfo.InvariantCulture, $"{x:0.###} {y:0.###} {z + 8:0.###}");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, obj.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            Log($"RespawnPos für {Server.MapName} angelegt ({obj["RespawnPos"]}) - !r funktioniert ab dem nächsten Rundenstart.");
        }
        catch (Exception ex)
        {
            Log($"Konnte RespawnPos nicht schreiben: {ex.Message}");
        }
    }

    private void Log(string msg) => Server.PrintToConsole($"[CourseFix] {msg}");
}
