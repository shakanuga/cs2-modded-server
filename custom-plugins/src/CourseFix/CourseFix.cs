using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Commands;
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
// 4. Runterfallen ohne zu sterben: Viele Gruben sind nicht tief genug für tödlichen
//    Fallschaden, man steht dann unten fest. -> Pro Map lässt sich eine Grubenhöhe
//    festlegen (Admin: in die Grube stellen, !setpit). Wer darunter fällt, kommt
//    automatisch zum letzten Checkpoint (!tp), ohne Checkpoint zum Start (!r).
//    Gespeichert in cfg/CourseFix/pits.json. !setpit off löscht sie, !pit zeigt sie.
public class CourseFix : BasePlugin
{
    public override string ModuleName => "CourseFix";
    public override string ModuleVersion => "1.5.0";
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

    // Grubenhöhe (Z) je Map aus cfg/CourseFix/pits.json, und wer gerade zurückgesetzt wird.
    private Dictionary<string, float> _pits = new();
    private float? _pitZ;
    private readonly HashSet<int> _rescued = new();

    private static readonly string[] StartTriggerNames = { "map_start", "s1_start", "stage1_start", "timer_startzone", "zone_start" };

    public override void Load(bool hotReload)
    {
        RegisterListener<Listeners.OnMapStart>(_ =>
        {
            _spawns.Clear();
            _reported.Clear();
            _spawnTime.Clear();
            _spawnDeathSeen = false;
            _rescued.Clear();
            LoadPits();
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
            {
                _spawnTime[p.Slot] = Server.CurrentTime;
                _rescued.Remove(p.Slot);
            }
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
        RegisterListener<Listeners.OnTick>(CheckFalls);
        LoadPits();

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

    private static string PitsPath() =>
        Path.Join(Server.GameDirectory, "csgo", "cfg", "CourseFix", "pits.json");

    // pits.json gehört dem Server (per !setpit geschrieben) und liegt nicht im Repo,
    // damit ein Update sie nicht überschreibt. Feste Werte aus dem Repo kommen aus
    // pits.default.json; Einträge in pits.json haben Vorrang.
    private Dictionary<string, float> ReadPits(string path) => File.Exists(path)
        ? JsonSerializer.Deserialize<Dictionary<string, float>>(File.ReadAllText(path)) ?? new()
        : new();

    private void LoadPits()
    {
        var merged = new Dictionary<string, float>();
        _pits = new();
        foreach (var (path, own) in new[] { (Path.ChangeExtension(PitsPath(), ".default.json"), false), (PitsPath(), true) })
        {
            try
            {
                foreach (var kv in ReadPits(path))
                {
                    merged[kv.Key] = kv.Value;
                    if (own) _pits[kv.Key] = kv.Value;
                }
            }
            catch (Exception ex)
            {
                Log($"{Path.GetFileName(path)} nicht lesbar: {ex.Message}");
            }
        }
        _pitZ = merged.TryGetValue(Server.MapName, out var z) ? z : null;
        if (_pitZ != null)
            Log($"Grubenhöhe für {Server.MapName}: {_pitZ:0}");
    }

    private void SavePits()
    {
        var path = PitsPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var sorted = new SortedDictionary<string, float>(_pits);
        File.WriteAllText(path, JsonSerializer.Serialize(sorted, new JsonSerializerOptions { WriteIndented = true }));
    }

    [ConsoleCommand("css_setpit", "Grubenhöhe für diese Map setzen: in die Grube stellen und !setpit, '!setpit off' löscht sie")]
    [RequiresPermissions("@css/rcon")]
    [CommandHelper(whoCanExecute: CommandUsage.CLIENT_ONLY)]
    public void OnSetPit(CCSPlayerController? player, CommandInfo info)
    {
        if (player == null || !player.IsValid)
            return;

        if (info.ArgCount > 1 && info.GetArg(1).Equals("off", StringComparison.OrdinalIgnoreCase))
        {
            _pits.Remove(Server.MapName);
            _pitZ = null;
            SavePits();
            info.ReplyToCommand($" {ChatColors.Green}[CourseFix]{ChatColors.Default} Grubenhöhe für {Server.MapName} gelöscht.");
            return;
        }

        var pos = player.PlayerPawn.Value?.AbsOrigin;
        if (pos == null || !player.PawnIsAlive)
        {
            info.ReplyToCommand($" {ChatColors.Green}[CourseFix]{ChatColors.Default} Nur lebend möglich.");
            return;
        }

        // Etwas über den Füßen, damit man beim Aufkommen sicher darunter ist.
        float z = MathF.Round(pos.Z + 32);
        _pits[Server.MapName] = z;
        _pitZ = z;
        SavePits();
        info.ReplyToCommand($" {ChatColors.Green}[CourseFix]{ChatColors.Default} Grubenhöhe für {Server.MapName} auf {z:0} gesetzt. Wer darunter fällt, kommt zum Checkpoint/Start zurück.");
        Log($"Grubenhöhe für {Server.MapName} auf {z:0} gesetzt von {player.PlayerName}");
    }

    [ConsoleCommand("css_pit", "Zeigt die Grubenhöhe dieser Map und deine aktuelle Höhe")]
    [CommandHelper(whoCanExecute: CommandUsage.CLIENT_ONLY)]
    public void OnPit(CCSPlayerController? player, CommandInfo info)
    {
        var pos = player?.PlayerPawn.Value?.AbsOrigin;
        string mine = pos == null ? "-" : $"{pos.Z:0}";
        string pit = _pitZ == null ? "nicht gesetzt" : $"{_pitZ:0}";
        info.ReplyToCommand($" {ChatColors.Green}[CourseFix]{ChatColors.Default} Grubenhöhe {Server.MapName}: {pit}, deine Höhe: {mine}");
    }

    private void CheckFalls()
    {
        if (_pitZ == null)
            return;
        float pitZ = _pitZ.Value;

        foreach (var player in Utilities.GetPlayers())
        {
            if (!player.IsValid || player.IsBot || !player.PawnIsAlive || _rescued.Contains(player.Slot))
                continue;
            var pawn = player.PlayerPawn.Value;
            var pos = pawn?.AbsOrigin;
            if (pawn == null || pos == null)
                continue;

            var move = pawn.MoveType.ToString();
            if (move.Contains("NOCLIP") || move.Contains("OBSERVER"))
                continue;

            if (pos.Z < pitZ)
            {
                _rescued.Add(player.Slot);
                Rescue(player);
            }
        }
    }

    // Zum letzten Checkpoint (SharpTimer !tp), sonst zum Start (!r).
    private void Rescue(CCSPlayerController player)
    {
        var start = player.PlayerPawn.Value?.AbsOrigin;
        var before = start == null ? (0f, 0f, 0f) : (start.X, start.Y, start.Z);
        player.ExecuteClientCommandFromServer("css_tp");

        AddTimer(0.3f, () =>
        {
            int slot = player.Slot;
            if (!player.IsValid || !player.PawnIsAlive)
            {
                _rescued.Remove(slot);
                return;
            }
            var now = player.PlayerPawn.Value?.AbsOrigin;
            bool moved = now != null && Math.Abs(now.Z - before.Item3) > 100 ||
                         now != null && (Math.Abs(now.X - before.Item1) > 100 || Math.Abs(now.Y - before.Item2) > 100);
            if (!moved)
                player.ExecuteClientCommandFromServer("css_r");

            // Kurz warten, bis der Teleport sicher durch ist.
            AddTimer(0.5f, () => _rescued.Remove(slot));
        });
    }

    private void Log(string msg) => Server.PrintToConsole($"[CourseFix] {msg}");
}
