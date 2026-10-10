// AutoAttack tracks entities that enter Attack_Range and hits them on a cooldown.
//
// Visibility (Only_In_Front) is sneak-aware and multi-point: the eye sits at 1.62 standing and 1.27 sneaking, and each target is tested at several heights (low/mid/high), counting as visible when any one of them is both inside the view cone and unoccluded.
// Single-point torso rays go blind through under-gaps (a cauldron's leg lanes) and a standing-height eye blinds a crouching player under a slab from inside the slab's own shape, so both are handled here, not by aiming.
// Note the server still enforces vanilla entity reach (3 blocks eye-to-box in survival) no matter what Attack_Range says: a wider range only tracks further, it cannot land hits further.
//
// Two inputs are missing on this core.
// Per-entity health is never populated, so Priority = health falls back to distance with a one-time warning, and a target counts as alive while it is still tracked (a dead entity is dropped from the entity store).
// The generic.attack_speed attribute is not tracked either, so the vanilla cooldown uses a fixed 4.0 attacks per second.

using DMCBK.PluginSdk;
using Microsoft.Extensions.Logging;
using Tomlet.Attributes;
using Umpk.Client.Actions;
using Umpk.Client.Events;
using Umpk.Data.Java;
using Umpk.Game.Entities;
using Umpk.Game.Players;
using Umpk.Game.Registries;
using Umpk.Game.World;
using Umpk.Geometry;

namespace DMCBK.OfficialPlugins.AutoAttack;

/// <summary>How many targets are hit per cooldown.</summary>
public enum AttackMode
{
    /// <summary>Hit one priority target.</summary>
    Single,

    /// <summary>Hit every tracked target.</summary>
    Multi,
}

/// <summary>How the single-mode priority target is chosen.</summary>
public enum PriorityType
{
    /// <summary>Closest first.</summary>
    Distance,

    /// <summary>Lowest health first. Per-entity health is not tracked, so this falls back to distance.</summary>
    Health,
}

/// <summary>How the configured entity list is applied.</summary>
public enum ListType
{
    /// <summary>Only entities in the list are attacked.</summary>
    Whitelist,

    /// <summary>Entities in the list are never attacked.</summary>
    Blacklist,
}

/// <summary>Attack (left click) or interact (right click).</summary>
public enum InteractType
{
    /// <summary>Left click.</summary>
    Attack,

    /// <summary>Right click.</summary>
    Interact,
}

/// <summary>Entity classification and target selection.</summary>
public static class AttackTargets
{
    // Registry paths rather than an enum, so the set stays version neutral across the supported range.
    private static readonly HashSet<string> Hostile = new(StringComparer.Ordinal)
    {
        "blaze", "bogged", "breeze", "cave_spider", "creeper", "drowned", "elder_guardian", "ender_dragon",
        "enderman", "endermite", "evoker", "ghast", "giant", "guardian", "hoglin", "husk", "illusioner",
        "magma_cube", "phantom", "piglin", "piglin_brute", "pillager", "ravager", "shulker", "silverfish",
        "skeleton", "slime", "spider", "stray", "vex", "vindicator", "warden", "witch", "wither",
        "wither_skeleton", "zoglin", "zombie", "zombie_villager", "zombified_piglin", "creaking",
    };

    private static readonly HashSet<string> Passive = new(StringComparer.Ordinal)
    {
        "allay", "armadillo", "axolotl", "bat", "bee", "camel", "cat", "chicken", "cod", "cow", "donkey",
        "fox", "frog", "glow_squid", "goat", "horse", "llama", "mooshroom", "mule", "ocelot", "panda",
        "parrot", "pig", "polar_bear", "pufferfish", "rabbit", "salmon", "sheep", "skeleton_horse", "sniffer",
        "snow_golem", "squid", "strider", "tadpole", "trader_llama", "tropical_fish", "turtle", "villager",
        "wandering_trader", "wolf", "zombie_horse", "iron_golem", "happy_ghast",
    };

    /// <summary>Strips the namespace from an entity id and lowercases it.</summary>
    public static string BarePath(string? typeId)
    {
        if (string.IsNullOrWhiteSpace(typeId))
            return string.Empty;

        string value = typeId.Trim();
        int colon = value.IndexOf(':');
        if (colon >= 0)
            value = value[(colon + 1)..];

        var chars = new char[value.Length];
        int length = 0;
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (char.IsUpper(c) && length > 0 && chars[length - 1] != '_')
            {
                if (length == chars.Length)
                    Array.Resize(ref chars, chars.Length + 8);

                chars[length++] = '_';
            }

            if (length == chars.Length)
                Array.Resize(ref chars, chars.Length + 8);

            chars[length++] = char.ToLowerInvariant(c);
        }

        return new string(chars, 0, length);
    }

    /// <summary>True when the entity type is in the hostile set.</summary>
    public static bool IsHostile(string typeId) => Hostile.Contains(BarePath(typeId));

    /// <summary>True when the entity type is in the passive set.</summary>
    public static bool IsPassive(string typeId) => Passive.Contains(BarePath(typeId));

    /// <summary>
    /// The hostile and passive class flags decide first, then a non-empty list either restricts (whitelist) or subtracts (blacklist).
    /// </summary>
    public static bool IsAllowedToAttack(
        string typeId, bool attackHostile, bool attackPassive, ListType listMode, IReadOnlyList<string> list)
    {
        bool result = (attackHostile && IsHostile(typeId)) || (attackPassive && IsPassive(typeId));
        if (list.Count == 0)
            return result;

        string bare = BarePath(typeId);
        bool inList = false;
        for (int i = 0; i < list.Count; i++)
        {
            if (string.Equals(bare, list[i], StringComparison.Ordinal))
            {
                inList = true;
                break;
            }
        }

        return listMode == ListType.Blacklist ? !inList && result : inList;
    }

    /// <summary>
    /// True when <paramref name="target"/> (feet position) lies inside the view cone around the player's look vector.
    /// The default <paramref name="fovDegrees"/> of 180 covers the forward hemisphere (any angle under 90 degrees).
    /// A narrower FOV trims the periphery: for example 90 only hits within 45 degrees of the crosshair.
    /// Yaw wraps naturally through sin/cos, so no normalization needed.
    /// </summary>
    /// <remarks>
    /// Kept feet-based with the standing eye for compatibility.
    /// The tick path does not call this: it tests each <see cref="AimHeights"/> sample through <see cref="IsPointInFront"/> with the sneak-aware eye, so a close target whose feet sit steeply below the crosshair still counts when its torso is in view.
    /// </remarks>
    public static bool IsInFront(Vec3d selfFeet, float yaw, float pitch, Vec3d target, double fovDegrees = 180.0)
        => IsPointInFront(PlayerReach.EyePosition(selfFeet), yaw, pitch, target, fovDegrees);

    /// <summary>
    /// True when <paramref name="point"/> lies inside the view cone around the look vector for <paramref name="yaw"/>/<paramref name="pitch"/>, measured from <paramref name="eye"/>.
    /// </summary>
    public static bool IsPointInFront(Vec3d eye, float yaw, float pitch, Vec3d point, double fovDegrees = 180.0)
    {
        Vec3d delta = point.Subtract(eye);

        // On top of the target: there is no direction to compare, so it counts as seen.
        if (delta.LengthSqr() < 1e-9)
            return true;

        // The vanilla look vector (Umpk.Physics PlayerPhysics.LookAngle).
        // Unit length.
        double yawRad = yaw * Math.PI / 180.0;
        double pitchRad = pitch * Math.PI / 180.0;
        double cosPitch = Math.Cos(pitchRad);
        double lookX = -Math.Sin(yawRad) * cosPitch;
        double lookY = -Math.Sin(pitchRad);
        double lookZ = Math.Cos(yawRad) * cosPitch;

        double dot = delta.X * lookX + delta.Y * lookY + delta.Z * lookZ;
        if (double.IsNaN(fovDegrees) || fovDegrees >= 180.0)
            // Full hemisphere: only the sign matters, so neither vector is normalized.
            return dot > 0;

        if (fovDegrees <= 0.0)
            return false;

        if (dot <= 0.0)
            return false;

        double halfRad = (fovDegrees * 0.5) * Math.PI / 180.0;
        double cosHalf = Math.Cos(halfRad);
        double length = Math.Sqrt(delta.LengthSqr());
        // A 1e-9 slack keeps an exactly-on-the-edge target inside despite float rounding (e.g. 5,1.62,5 at FOV 90 is exactly 45 degrees off).
        return dot / length >= cosHalf - 1e-9;
    }

    /// <summary>Clamps a total FOV in degrees to the usable 1-180 range. NaN restores 180.</summary>
    public static double ClampFov(double fovDegrees)
        => double.IsNaN(fovDegrees) ? 180.0 : Math.Clamp(fovDegrees, 1.0, 180.0);

    /// <summary>
    /// How far above an entity's feet the sight ray aims.
    /// UMPK tracks no per-type dimensions, so this is mid-torso for the common mobs (zombie, skeleton, cow, player) rather than any one type's centre.
    /// Mirrors <c>WorldApi.VisibilityAimHeight</c>; the two cannot share code (the plugin must not reference DMCBK.Core), so a change here must change there too.
    /// </summary>
    internal const double AimHeight = 0.9;

    /// <summary>
    /// The standing eye height above the feet.
    /// Mirrors <c>Umpk.Physics.PhysicsConstants.PlayerStandingEyeHeight</c>; duplicated (not referenced) because the plugin resolves the eye from <c>SelfState.Sneaking</c> and must not grow a physics dependency for two constants.
    /// A change in UMPK must change these too.
    /// </summary>
    internal const double StandingEyeHeight = 1.62;

    /// <summary>
    /// The sneaking eye height above the feet (mirrors <c>Umpk.Physics.PhysicsConstants.PlayerCrouchEyeHeight</c>; see <see cref="StandingEyeHeight"/>).
    /// Using the standing height while sneaking puts the computed eye 0.35 too high, which lands it inside a low ceiling slab and blinds every ray at its origin.
    /// </summary>
    internal const double CrouchingEyeHeight = 1.27;

    /// <summary>The eye height for a sneaking or standing player.</summary>
    public static double EyeHeight(bool sneaking) => sneaking ? CrouchingEyeHeight : StandingEyeHeight;

    /// <summary>The eye position for feet at <paramref name="feet"/>.</summary>
    public static Vec3d EyePosition(Vec3d feet, bool sneaking) => feet.Add(0, EyeHeight(sneaking), 0);

    /// <summary>
    /// Heights above a target's feet the sight ray samples, low to high.
    /// The low sample sees through under-gaps the torso ray cannot (a cauldron stands on legs with open lanes below y 0.188, a bottom slab leaves the upper half open); the mid sample is the <see cref="AimHeight"/>; the high sample covers tall mobs behind low cover.
    /// A target counts as visible when ANY sample is inside the view cone and unoccluded.
    /// </summary>
    internal static readonly double[] AimHeights = [0.2, AimHeight, 1.5];

    /// <summary>
    /// True when the segment from <paramref name="eye"/> to <paramref name="aim"/> enters no block: fluids never count, and a null <paramref name="shapes"/> reads every non-air block as a full cube (the shape tables are per-protocol generated data; tests pass null, production passes the negotiated session's).
    /// Unloaded chunks read as air, so unknown terrain never blocks.
    /// </summary>
    public static bool IsOccluded(World world, Vec3d eye, Vec3d aim, IBlockShapeSource? shapes)
    {
        ArgumentNullException.ThrowIfNull(world);
        return Raycast.CastBlock(world, eye, aim, includeFluids: false, shapes).Hit;
    }

    /// <summary>
    /// The full visibility gate: inside the view cone AND not behind blocks.
    /// A null <paramref name="world"/> means no terrain to test against (the Terrain feature is off), which reads as visible: unknown is not a wall.
    /// </summary>
    /// <remarks>
    /// Tests every <see cref="AimHeights"/> sample with the sneak-aware eye: a target is visible when any sample is both in the cone and unoccluded, so partial cover (a cauldron's under-gap, a slab edge) hides only the samples it actually blocks.
    /// Kept on the feet-based <see cref="IsInFront"/> contract for the null-world fallback only insofar as every sample shares the same cone test.
    /// </remarks>
    public static bool HasLineOfSight(
        World? world, Vec3d selfFeet, float yaw, float pitch, Vec3d targetFeet, IBlockShapeSource? shapes = null, double fovDegrees = 180.0)
        => HasLineOfSight(world, selfFeet, yaw, pitch, targetFeet, shapes, fovDegrees, sneaking: false);

    /// <summary>
    /// The full visibility gate with the player's sneak state: sneaking lowers the eye from <see cref="StandingEyeHeight"/> to <see cref="CrouchingEyeHeight"/>, which is what keeps a crouching player under a slab from raycasting out of the slab's own interior.
    /// </summary>
    public static bool HasLineOfSight(
        World? world, Vec3d selfFeet, float yaw, float pitch, Vec3d targetFeet, IBlockShapeSource? shapes, double fovDegrees, bool sneaking)
    {
        Vec3d eye = EyePosition(selfFeet, sneaking);
        foreach (double height in AimHeights)
        {
            Vec3d aim = targetFeet.Add(0, height, 0);
            if (!IsPointInFront(eye, yaw, pitch, aim, fovDegrees))
                continue;

            if (world is null)
                return true;

            if (!IsOccluded(world, eye, aim, shapes))
                return true;
        }

        return false;
    }

    /// <summary>Converts a cooldown in seconds to whole client ticks: truncate, add one, never below one.</summary>
    public static int SecondsToCooldownTicks(double seconds, int ticksPerSecond = 20)
    {
        seconds = Math.Min(int.MaxValue / (double)ticksPerSecond, seconds);
        return Math.Max(1, (int)Math.Truncate(seconds * ticksPerSecond) + 1);
    }

    /// <summary>Normalizes a list of entity names to bare snake_case paths, dropping blanks.</summary>
    public static string[] NormalizeList(IEnumerable<string>? names)
    {
        if (names is null)
            return [];

        var result = new List<string>();
        foreach (string name in names)
        {
            string bare = BarePath(name);
            if (bare.Length > 0 && !result.Contains(bare))
                result.Add(bare);
        }

        return [.. result];
    }

    /// <summary>Parses an attack mode name (single or multi).</summary>
    public static AttackMode ParseMode(string? value)
        => string.Equals(value?.Trim(), "multi", StringComparison.OrdinalIgnoreCase) ? AttackMode.Multi : AttackMode.Single;

    /// <summary>Parses a priority name (distance or health).</summary>
    public static PriorityType ParsePriority(string? value)
        => string.Equals(value?.Trim(), "health", StringComparison.OrdinalIgnoreCase) ? PriorityType.Health : PriorityType.Distance;

    /// <summary>Parses a list mode name (whitelist or blacklist).</summary>
    public static ListType ParseListType(string? value)
        => string.Equals(value?.Trim(), "blacklist", StringComparison.OrdinalIgnoreCase) ? ListType.Blacklist : ListType.Whitelist;

    /// <summary>Parses an interaction name (Attack or Interact).</summary>
    public static InteractType ParseInteraction(string? value)
        => string.Equals(value?.Trim(), "interact", StringComparison.OrdinalIgnoreCase) ? InteractType.Interact : InteractType.Attack;
}

/// <summary>The user-editable, persisted settings for AutoAttack (serialized to settings.toml).</summary>
public sealed class AutoAttackSettings : IValidatablePluginSettings
{
    /// <summary>Master switch.</summary>
    [TomlInlineComment("$settings.enabled$")]
    public bool Enabled { get; set; }

    /// <summary>single = one priority target per cooldown, multi = every target in range.</summary>
    [TomlInlineComment("$settings.mode$")]
    public string Mode { get; set; } = "single";

    /// <summary>distance = closest first, health = lowest health first (falls back to distance).</summary>
    [TomlInlineComment("$settings.priority$")]
    public string Priority { get; set; } = "distance";

    /// <summary>Attack or Interact.</summary>
    [TomlInlineComment("$settings.interaction$")]
    public string Interaction { get; set; } = "Attack";

    /// <summary>Attack reach in blocks, clamped to 1.0 - 4.0.</summary>
    [TomlInlineComment("$settings.attack_range$")]
    public double Attack_Range { get; set; } = 4.0;

    /// <summary>Only attack entities you can see. False restores range-only behavior.</summary>
    [TomlInlineComment("$settings.only_in_front$")]
    public bool Only_In_Front { get; set; } = true;

    /// <summary>Total view-cone width in degrees. 180 is the full hemisphere, narrower trims the periphery.</summary>
    [TomlInlineComment("$settings.front_fov$")]
    public double Front_FOV { get; set; } = 180.0;

    /// <summary>Attack entities in the hostile set.</summary>
    [TomlInlineComment("$settings.attack_hostile$")]
    public bool Attack_Hostile { get; set; } = true;

    /// <summary>Attack entities in the passive set.</summary>
    [TomlInlineComment("$settings.attack_passive$")]
    public bool Attack_Passive { get; set; }

    /// <summary>whitelist restricts to Entites_List, blacklist subtracts it.</summary>
    [TomlInlineComment("$settings.list_mode$")]
    public string List_Mode { get; set; } = "whitelist";

    /// <summary>The entity type list. The misspelled name is kept on purpose: it is the settings key.</summary>
    [TomlPrecedingComment("$settings.entites_list$")]
    public string[] Entites_List { get; set; } = ["Zombie", "Cow"];

    /// <summary>The cooldown configuration.</summary>
    public CooldownConfig Cooldown_Time { get; set; } = new();

    /// <summary>Clamps and normalizes on load and reload.</summary>
    public void Validate()
    {
        Entites_List ??= [];
        Cooldown_Time ??= new CooldownConfig();
        Mode = AutoAttackTextNames.Mode(AttackTargets.ParseMode(Mode));
        Priority = AutoAttackTextNames.Priority(AttackTargets.ParsePriority(Priority));
        List_Mode = AutoAttackTextNames.ListType(AttackTargets.ParseListType(List_Mode));
        Interaction = AutoAttackTextNames.Interaction(AttackTargets.ParseInteraction(Interaction));

        if (Cooldown_Time.Custom)
        {
            if (Cooldown_Time.Min <= 0)
                Cooldown_Time.Min = 0.1;

            if (Cooldown_Time.Max <= 0)
                Cooldown_Time.Max = 0.1;

            if (Cooldown_Time.Min > Cooldown_Time.Max)
                (Cooldown_Time.Min, Cooldown_Time.Max) = (Cooldown_Time.Max, Cooldown_Time.Min);
        }

        if (Attack_Range < 1.0)
            Attack_Range = 1.0;

        if (Attack_Range > 4.0)
            Attack_Range = 4.0;

        Front_FOV = AttackTargets.ClampFov(Front_FOV);
    }
}

/// <summary>The Cooldown_Time sub-table.</summary>
public sealed class CooldownConfig
{
    /// <summary>Use the configured cooldown instead of the vanilla attack-speed one.</summary>
    [TomlInlineComment("$settings.cooldown_custom$")]
    public bool Custom { get; set; }

    /// <summary>Pick a random cooldown between Min and Max on every swing.</summary>
    [TomlInlineComment("$settings.cooldown_random_mode$")]
    public bool RandomMode { get; set; }

    /// <summary>The minimum cooldown in seconds.</summary>
    [TomlInlineComment("$settings.cooldown_min$")]
    public double Min { get; set; } = 1.5;

    /// <summary>The maximum cooldown in seconds.</summary>
    [TomlInlineComment("$settings.cooldown_max$")]
    public double Max { get; set; } = 2.5;
}

/// <summary>The spellings the enum settings are written back as.</summary>
internal static class AutoAttackTextNames
{
    internal static string Mode(AttackMode mode) => mode == AttackMode.Multi ? "multi" : "single";

    internal static string Priority(PriorityType priority) => priority == PriorityType.Health ? "health" : "distance";

    internal static string ListType(ListType list) => list == DMCBK.OfficialPlugins.AutoAttack.ListType.Blacklist ? "blacklist" : "whitelist";

    internal static string Interaction(InteractType interaction)
        => interaction == InteractType.Interact ? "Interact" : "Attack";
}

/// <summary>The AutoAttack plugin entry point.</summary>
public sealed class AutoAttackPlugin : IPlugin
{
    private const double DefaultAttackSpeed = 4.0;

    private readonly Random _random = new();
    private readonly HashSet<int> _targets = [];

    private AutoAttackSettings _settings = new();
    private ILogger? _logger;
    private IPluginLocalization? _strings;
    private string[] _list = [];
    private AttackMode _mode = AttackMode.Single;
    private ListType _listMode = ListType.Whitelist;
    private InteractType _interaction = InteractType.Attack;
    private bool _onlyInFront = true;
    private double _frontFov = 180.0;
    private int _cooldownTicks = 1;
    private int _counter;
    private bool _warnedHealthPriority;

    /// <inheritdoc/>
    public void Configure(PluginDescriptor descriptor)
    {
        descriptor.Id = "auto-attack";
        descriptor.Version = "2.0.0";
        descriptor.ApiVersion = PluginApiVersion.Major;
        descriptor.WithSettings<AutoAttackSettings>();
    }

    /// <inheritdoc/>
    public Task ActivateAsync(PluginContext context)
    {
        _settings = context.Settings.Load<AutoAttackSettings>();
        _logger = context.Logger;
        _strings = context.Strings;

        _list = AttackTargets.NormalizeList(_settings.Entites_List);
        _mode = AttackTargets.ParseMode(_settings.Mode);
        _listMode = AttackTargets.ParseListType(_settings.List_Mode);
        _interaction = AttackTargets.ParseInteraction(_settings.Interaction);
        _onlyInFront = _settings.Only_In_Front;
        _frontFov = AttackTargets.ClampFov(_settings.Front_FOV);
        _cooldownTicks = AttackTargets.SecondsToCooldownTicks(
            _settings.Cooldown_Time.Custom ? _settings.Cooldown_Time.Min : 1.0 / DefaultAttackSpeed);

        context.SessionStarted += (_, e) => OnSessionStarted(e.Session);
        context.SessionEnded += (_, _) => _targets.Clear();
        return Task.CompletedTask;
    }

    private void OnSessionStarted(ISessionScope session)
    {
        _targets.Clear();
        _counter = _cooldownTicks;

        if (!session.State.Features.Entities)
        {
            _logger?.LogWarning("{Message}", _strings?.Get("entities_required"));
            return;
        }

        session.Events.Subscribe<EntitySpawned>(e => Track(session, e.EntityId));
        session.Events.Subscribe<EntityMoved>(e => Track(session, e.EntityId));
        session.Events.Subscribe<EntityRemoved>(e => _targets.Remove(e.EntityId));
        session.Events.Subscribe<Died>(_ => _targets.Clear());
        session.Scheduler.OnTick(() => OnTick(session));
    }

    private void Track(ISessionScope session, int entityId)
    {
        if (!_settings.Enabled || !session.State.Entities.TryGet(entityId, out Entity? entity))
            return;

        ShouldAttack(session, entity);
    }

    /// <summary>
    /// Re-scans every tracked entity each swing.
    /// Spawn/move events alone miss stationary targets the bot walks up to (or that were out of range the last time they moved), so the tick re-discovers them here; <see cref="ShouldAttack"/> adds the in-range ones and drops the out-of-range ones.
    /// </summary>
    private void RefreshTargets(ISessionScope session)
    {
        foreach (Entity entity in session.State.Entities.All)
            ShouldAttack(session, entity);
    }

    private bool ShouldAttack(ISessionScope session, Entity entity)
    {
        if (!AttackTargets.IsAllowedToAttack(
                entity.Type.Id.ToString(), _settings.Attack_Hostile, _settings.Attack_Passive, _listMode, _list))
            return false;

        double distance = entity.Position.Subtract(session.State.Self.Position).Length();
        if (distance <= _settings.Attack_Range)
        {
            _targets.Add(entity.Id);
            return true;
        }

        _targets.Remove(entity.Id);
        return false;
    }

    /// <summary>
    /// The visibility gate.
    /// Off means range-only checks with no visibility test.
    /// A target that fails stays
    /// tracked (turning to face it, or the wall coming down, must work without it moving first);
    /// it is just skipped at swing time.
    /// No terrain (the Terrain feature is off) reads as visible.
    /// </summary>
    private bool ClearlyVisible(ISessionScope session, Entity entity)
    {
        if (!_onlyInFront)
            return true;

        var self = session.State.Self;
        var client = session.Client;
        World? world = client.State.HasWorld && client.Session is not null ? client.State.World : null;
        IBlockShapeSource? shapes = client.Session is not null
            ? JavaGameData.BlockShapes(client.Session.Version.Version.Protocol)
            : null;
        return AttackTargets.HasLineOfSight(world, self.Position, self.Yaw, self.Pitch, entity.Position, shapes, _frontFov, self.Sneaking);
    }

    private void OnTick(ISessionScope session)
    {
        if (!_settings.Enabled || session.State.Self.Health <= 0)
            return;

        if (_counter > 0)
        {
            _counter--;
            return;
        }

        if (_settings.Cooldown_Time.Custom && _settings.Cooldown_Time.RandomMode)
        {
            double seconds = (_random.NextDouble() * (_settings.Cooldown_Time.Max - _settings.Cooldown_Time.Min))
                + _settings.Cooldown_Time.Min;
            _cooldownTicks = AttackTargets.SecondsToCooldownTicks(seconds);
        }

        _counter = _cooldownTicks;
        RefreshTargets(session);
        if (_targets.Count == 0)
            return;

        if (_mode == AttackMode.Single)
        {
            Entity? best = PickPriorityTarget(session);
            if (best is not null)
                _ = HitAsync(session, best, swing: true);

            return;
        }

        var snapshot = new List<int>(_targets);
        bool anyHit = false;
        foreach (int id in snapshot)
        {
            if (!session.State.Entities.TryGet(id, out Entity? entity) || !ShouldAttack(session, entity))
            {
                // ShouldAttack already dropped the out-of-range ones; the missing ones go here.
                _targets.Remove(id);
                continue;
            }

            if (!ClearlyVisible(session, entity))
                continue;

            _ = HitAsync(session, entity, swing: false);
            anyHit = true;
        }

        if (anyHit)
            _ = session.Actions.Movement.SwingArmAsync(session.Detached);
    }

    private Entity? PickPriorityTarget(ISessionScope session)
    {
        // Health priority needs per-entity health, which this core does not track: warn once, use distance.
        if (AttackTargets.ParsePriority(_settings.Priority) == PriorityType.Health && !_warnedHealthPriority)
        {
            _warnedHealthPriority = true;
            _logger?.LogWarning("{Message}", _strings?.Get("health_priority_unavailable"));
        }

        Vec3d self = session.State.Self.Position;
        Entity? best = null;
        double bestDistance = double.MaxValue;
        var stale = new List<int>();

        foreach (int id in _targets)
        {
            if (!session.State.Entities.TryGet(id, out Entity? entity))
            {
                stale.Add(id);
                continue;
            }

            double distance = entity.Position.Subtract(self).Length();
            if (distance > _settings.Attack_Range)
            {
                stale.Add(id);
                continue;
            }

            if (!ClearlyVisible(session, entity))
                continue;

            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = entity;
            }
        }

        foreach (int id in stale)
            _targets.Remove(id);

        return best;
    }

    private async Task HitAsync(ISessionScope session, Entity entity, bool swing)
    {
        try
        {
            if (_interaction == InteractType.Interact)
            {
                await session.Actions.Interaction.InteractEntityAsync(entity.Id, Hand.Main, session.Detached)
                    .ConfigureAwait(false);
            }
            else
                await session.Actions.Interaction.AttackEntityAsync(entity.Id, session.Detached).ConfigureAwait(false);

            if (swing)
                await session.Actions.Movement.SwingArmAsync(session.Detached).ConfigureAwait(false);

            _logger?.LogInformation(
                "{Message}",
                _strings?.Format("attacked", entity.Type.Id.ToString(), entity.Id));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogDebug(ex, "{Message}", _strings?.Get("hit_failed"));
        }
    }
}
