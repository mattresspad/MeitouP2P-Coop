using System.Numerics;
using System.Reflection;
using System.Text.Json;
using HarmonyLib;
using Meitou.Game.Ui;
using Meitou.Modding;
using Meitou.Simulation;

namespace MeitouP2P.Coop;

// The Stoat fixture has exactly one player character of each of these templates.
internal sealed class StoatWalkTrial : IUiExtension
{
    internal const string ScoutRecord = "1-Stoat Walk.mod";
    private const string GuideRecord = "2-Stoat Walk.mod";
    private const string Hint = "Select Scout, then right-click the ground";
    private sealed record Pose(float X, float Y, float Z, float Yaw, float? DrawYaw, AnimationLayer[] Layers);
    private sealed record Capture(World World, long Tick, CharacterId Actor, Pose Pose);
    private sealed record Sample(Guid Grant, long Tick, Pose Scout, Pose Guide);
    private sealed record Destination(Guid Grant, Vector3 Point);
    private static StoatWalkTrial? active;
    private readonly ISessionApi session;
    private readonly ModContext context;
    private readonly int port;
    private readonly object gate = new();
    private Capture? scout, guide;
    private (Capture Scout, Capture Guide)? complete;
    private Sample? remote, drawn;
    private long drawTick = -1;
    private Destination? pending;
    private Task<string>? request;
    private bool requestingMove, connecting, connected;
    private int worldNumber, requestWorld;
    private string status = "Ready", feedback = Hint;
    private long nextPoll, feedbackUntil;
    private MethodInfo? selected;
    internal bool Host { get; }
    public string Id => "stoat.walk";

    private StoatWalkTrial(ModContext context, ISessionApi session, bool host, int port)
    {
        this.context = context;
        this.session = session;
        this.port = port;
        Host = host;
    }

    internal static StoatWalkTrial? Open(ModContext context, ISessionApi session)
    {
        string? role = Environment.GetEnvironmentVariable("MEITOU_STOAT_WALK_ROLE");
        if (role is not ("host" or "guest") || context.Data.Find(ScoutRecord) == null || context.Data.Find(GuideRecord) == null) return null;
        if (!int.TryParse(Environment.GetEnvironmentVariable("MEITOU_STOAT_WALK_PORT"), out var port) || port is < 1 or > 65535)
            throw new ArgumentException("Set the Stoat trial loopback port.");
        var t = new StoatWalkTrial(context, session, role == "host", port);
        if (!t.Host)
        {
            var player = typeof(IMod).Assembly.GetType("Meitou.Game.PlayerInterface", throwOnError: true)!;
            t.selected = AccessTools.DeclaredMethod(player, "Selected", Type.EmptyTypes) ?? throw new MissingMethodException(player.FullName, "Selected");
            context.Harmony.Patch(AccessTools.DeclaredMethod(player, "Say", [typeof(SimCommand)]),
                prefix: new HarmonyMethod(typeof(StoatWalkTrial), nameof(EmptySelection)));
            context.Harmony.Patch(AccessTools.DeclaredMethod(player, "Move", [typeof(Vector3), typeof(bool)]),
                prefix: new HarmonyMethod(typeof(StoatWalkTrial), nameof(GroundMove)));
        }
        context.Harmony.Patch(AccessTools.DeclaredMethod(typeof(World), "FillIdentity"),
            postfix: new HarmonyMethod(typeof(StoatWalkTrial), nameof(Published)));
        active = t;
        context.AddUi(t);
        return t;
    }

    private void ClearGuest()
    {
        pending = null;
        remote = drawn = null;
        drawTick = -1;
        feedback = Hint;
        feedbackUntil = 0;
    }

    internal void ResetWorld()
    {
        lock (gate)
        {
            worldNumber++;
            scout = guide = null;
            complete = null;
            ClearGuest();
        }
    }

    internal CharacterId? ScoutIn(World world)
    {
        lock (gate) return scout is { } s && ReferenceEquals(s.World, world) ? s.Actor : null;
    }

    internal string HostPose(World world, CharacterId actor, Guid grant)
    {
        Sample sample;
        lock (gate)
        {
            if (complete is not { } pair || pair.Scout.Actor != actor || pair.Scout.World != world)
                return "Waiting for both Wanderers";
            sample = new(grant, pair.Scout.Tick, pair.Scout.Pose, pair.Guide.Pose);
        }
        return JsonSerializer.Serialize(sample);
    }

    private static Action<CharacterSnapshot,T> Setter<T>(string property) =>
        typeof(CharacterSnapshot).GetProperty(property)!.GetSetMethod(true)!.CreateDelegate<Action<CharacterSnapshot,T>>();
    private static readonly Action<CharacterSnapshot,Vector3> SetPosition = Setter<Vector3>(nameof(CharacterSnapshot.Position));
    private static readonly Action<CharacterSnapshot,float> SetYaw = Setter<float>(nameof(CharacterSnapshot.Yaw));
    private static readonly Action<CharacterSnapshot,float> SetDrawYaw = Setter<float>(nameof(CharacterSnapshot.DrawYaw));
    private static readonly Action<CharacterSnapshot,IReadOnlyList<AnimationLayer>> SetLayers = Setter<IReadOnlyList<AnimationLayer>>(nameof(CharacterSnapshot.Animations));

    private static void Published(World __instance, CharacterSnapshot s)
    {
        if (!s.IsPlayer || s.RecordId is not (ScoutRecord or GuideRecord) || active is not { } trial) return;
        lock (trial.gate)
        {
            if (trial.Host)
            {
                var p = s.Position;
                var captured = new Capture(__instance, __instance.Tick, s.Id, new(p.X, p.Y, p.Z, s.Yaw,
                    float.IsFinite(s.DrawYaw) ? s.DrawYaw : null, s.Animations.ToArray()));
                if (s.RecordId == ScoutRecord) trial.scout = captured;
                else trial.guide = captured;
                if (trial.scout is { } scout && trial.guide is { } guide && scout.World == guide.World && scout.Tick == guide.Tick)
                    trial.complete = (scout, guide);
                return;
            }
            // Both snapshot workers use the same received sample for the whole guest tick.
            if (__instance.Tick != trial.drawTick)
            {
                trial.drawTick = __instance.Tick;
                trial.drawn = trial.remote;
            }
            if (trial.drawn is not { } sample) return;
            var pose = s.RecordId == ScoutRecord ? sample.Scout : sample.Guide;
            SetPosition(s, new(pose.X, pose.Y, pose.Z));
            SetYaw(s, pose.Yaw);
            SetDrawYaw(s, pose.DrawYaw ?? float.NaN);
            SetLayers(s, pose.Layers);
        }
    }

    private static void EmptySelection(ref SimCommand command)
    {
        if (active is { Host: false, connecting: true } && command is SelectCommand { Characters.Count: 0, Additive: false } empty)
            command = empty with { Clear = true };
    }

    private static bool GroundMove(object __instance, Vector3 target, bool queued, bool ___Steering)
    {
        if (active is not { Host: false } t) return true;
        lock (t.gate)
        {
            if (!t.connecting) return true;
            // A press has already supplied the destination. Steer() repeats Move every tick until release.
            if (___Steering) return false;
            var selection = (IReadOnlyList<CharacterSnapshot>)t.selected!.Invoke(__instance, null)!;
            if (selection.Count != 1 || selection[0].RecordId != ScoutRecord)
                t.Feedback("Only Stoat Scout can take guest orders.");
            else if (queued) t.Feedback("Use a plain right-click for this walk.");
            else if (t.remote is not { } frame) t.Feedback("Waiting for the host to assign Stoat Scout.");
            else
            {
                t.pending = new(frame.Grant, target);
                t.Feedback("Scout destination selected.");
            }
            return false;
        }
    }

    private void Feedback(string message)
    {
        if (message != feedback || Environment.TickCount64 >= feedbackUntil) context.Log(message);
        feedback = message;
        feedbackUntil = Environment.TickCount64 + 3500;
    }

    public void Register(UiRegistration r)
    {
        string connect = r.RegisterAction("connect", _ =>
        {
            lock (gate)
            {
                try { status = session.Start(Host, port); connecting = true; }
                catch (Exception e) { status = e.GetBaseException().Message; }
            }
        });
        string style = r.RegisterStyle("plate", new UiStyle { Background = new(.10f,.08f,.06f,.94f), TextPadding = new(8,2,8,2) });
        r.Contribute(UiSlots.Overlay, _ =>
        {
            lock (gate)
            {
                Pump();
                string side = Host ? "HOST" : "GUEST";
                var panel = new UiNode(r.Key("trial-panel")) { Placement = new(475,63, Left:12,Top:12), Pickable = false };
                panel.Children.Add(new UiNode(r.Key("status"), UiNodeKind.Label) { Text = side + "   " + status, Style = style, Placement = new(360,28) });
                panel.Children.Add(new UiNode(r.Key("session"), UiNodeKind.Button) {
                    Text = Host ? "Host session" : "Join host", Style = style, Action = connect, Placement = new(110,29, Top:31) });
                if (!Host) panel.Children.Add(new UiNode(r.Key("hint"), UiNodeKind.Label) {
                    Text = Environment.TickCount64 < feedbackUntil ? feedback : Hint, Style = style, Placement = new(340,29, Left:115, Top:31) });
                return panel;
            }
        });
    }

    // The UI thread owns the single outstanding request. ResetWorld invalidates its reply.
    private void Pump()
    {
        if (!connecting) return;
        if (Host)
        {
            if (session.HostedPeer != null) status = "Sharing Scout and Guide";
            else status = "Waiting for guest";
            return;
        }
        if (request is { IsCompleted: false }) return;
        if (request != null)
        {
            try
            {
                string reply = request.GetAwaiter().GetResult();
                if (requestWorld == worldNumber)
                {
                    connected = true;
                    Receive(reply);
                }
            }
            catch (Exception e)
            {
                if (requestWorld == worldNumber)
                {
                    status = e.GetBaseException().Message;
                    if (status != "A request is in progress.")
                    {
                        ClearGuest();
                        if (connected) connecting = false;
                        connected = false;
                    }
                }
            }
            request = null;
        }
        if (!connecting) return;
        long now = Environment.TickCount64;
        if (pending == null && now < nextPoll) return;
        nextPoll = now + 50;
        requestWorld = worldNumber;
        string message = "poses";
        requestingMove = false;
        if (pending is { } move && remote?.Grant == move.Grant)
        {
            var p = move.Point;
            message = FormattableString.Invariant($"move {move.Grant} {p.X:R} {p.Y:R} {p.Z:R}");
            context.Log(FormattableString.Invariant($"Guest ground move for Scout to ({p.X:F2},{p.Z:F2})"));
            requestingMove = true;
        }
        pending = null;
        request = session.RequestAsync("meitou.p2p.coop", message);
    }

    private void Receive(string reply)
    {
        if (requestingMove)
        {
            if (!reply.StartsWith("Move received", StringComparison.Ordinal)) { ClearGuest(); Feedback(reply); }
            return;
        }
        if (!reply.StartsWith('{')) { ClearGuest(); status = reply; return; }
        var frame = JsonSerializer.Deserialize<Sample>(reply)!;
        if (remote?.Grant != frame.Grant) ClearGuest();
        remote = frame;
        status = "Scout and Guide   ·   host tick " + frame.Tick;
    }
}
