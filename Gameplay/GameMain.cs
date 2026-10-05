using System.Collections.Generic;
using System.Linq;
using BuildNDestroy.Network;
using Godot;
using PbGameState = Game.GameState;
using PbServerEvent = Game.ServerEvent;

namespace BuildNDestroy.Gameplay;

public partial class GameMain : Node3D
{
    [Export] public float MouseSensitivity { get; set; } = 0.002f;
    [Export] public float CameraDistance { get; set; } = 4.5f;
    [Export] public float CameraHeight { get; set; } = 1.4f;

    // Must match the server's movement constants (see server/internal/game/room.go).
    private const float ServerMoveSpeed = 5f;
    private const float ServerTickDt = 1f / 60f;

    private NetworkClient _net = null!;
    private Camera3D _camera = null!;
    private Node3D _playersRoot = null!;
    private Node3D _debugRoot = null!;

    private readonly Dictionary<string, MeshInstance3D> _remotePlayers = new();
    private Label _hudPlayers = null!;
    private Label3D _localLabel = null!;

    // Four debug markers for the local player.
    private MeshInstance3D _markerCurLocal = null!;
    private MeshInstance3D _markerPrevLocal = null!;
    private MeshInstance3D _markerCurServer = null!;
    private MeshInstance3D _markerPrevServer = null!;

    private Vector3 _curLocalPos;
    private Vector3 _prevLocalPos;
    private Vector3 _curServerPos;
    private Vector3 _prevServerPos;
    private Vector3 _lastWorldMove;
    private bool _hasServerPos;

    private float _yaw;
    private float _pitch = -0.2f;

    public override void _Ready()
    {
        _net = GetNode<NetworkClient>("NetworkClient");
        _camera = GetNode<Camera3D>("Camera3D");
        _playersRoot = GetNode<Node3D>("Players");

        _debugRoot = new Node3D { Name = "LocalDebug" };
        AddChild(_debugRoot);

        _markerCurLocal   = CreateMarker("CurLocal",   new Color(0.1f, 0.9f, 1.0f, 0.55f));
        _markerPrevLocal  = CreateMarker("PrevLocal",  new Color(0.0f, 0.4f, 0.6f, 0.45f));
        _markerCurServer  = CreateMarker("CurServer",  new Color(0.2f, 1.0f, 0.2f, 0.55f));
        _markerPrevServer = CreateMarker("PrevServer", new Color(1.0f, 0.9f, 0.1f, 0.45f));

        _localLabel = CreateIdLabel("me?");
        _markerCurServer.AddChild(_localLabel);

        var hud = new CanvasLayer();
        AddChild(hud);
        _hudPlayers = new Label
        {
            Position = new Vector2(12, 10),
            Text = "players: 0",
            Modulate = new Color(1f, 1f, 1f, 0.85f),
        };
        hud.AddChild(_hudPlayers);

        Input.MouseMode = Input.MouseModeEnum.Captured;
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is InputEventMouseMotion motion && Input.MouseMode == Input.MouseModeEnum.Captured)
        {
            _yaw -= motion.Relative.X * MouseSensitivity;
            _pitch -= motion.Relative.Y * MouseSensitivity;
            _pitch = Mathf.Clamp(_pitch, -Mathf.Pi / 2f + 0.01f, Mathf.Pi / 2f - 0.01f);
        }

        if (@event is InputEventMouseButton mb && mb.Pressed
            && Input.MouseMode != Input.MouseModeEnum.Captured)
        {
            Input.MouseMode = Input.MouseModeEnum.Captured;
            GetViewport().SetInputAsHandled();
        }

        if (@event is InputEventKey key && key.Pressed && !key.Echo
            && key.Keycode == Key.Escape)
        {
            Input.MouseMode = Input.MouseModeEnum.Visible;
        }
    }

    public override void _Notification(int what)
    {
        if (what == NotificationApplicationFocusOut || what == NotificationWMWindowFocusOut)
        {
            Input.MouseMode = Input.MouseModeEnum.Visible;
        }
    }

    public override void _PhysicsProcess(double delta)
    {
        while (_net.TryDequeue(out var ev) && ev != null)
            HandleEvent(ev);

        if (!_net.IsJoined) return;

        var local = Vector3.Zero;
        if (Input.IsKeyPressed(Key.W)) local += Vector3.Forward;
        if (Input.IsKeyPressed(Key.S)) local += Vector3.Back;
        if (Input.IsKeyPressed(Key.A)) local += Vector3.Left;
        if (Input.IsKeyPressed(Key.D)) local += Vector3.Right;
        if (local.LengthSquared() > 0f) local = local.Normalized();

        var yawBasis = Basis.FromEuler(new Vector3(0f, _yaw, 0f));
        var worldMove = yawBasis * local;
        _lastWorldMove = worldMove;

        _net.SendInput(worldMove, _yaw, _pitch);

        if (_hasServerPos)
        {
            // Mirror the server's movement formula locally — predicts where the
            // player will probably be after the server processes this input.
            _prevLocalPos = _curLocalPos;
            _curLocalPos += worldMove * ServerMoveSpeed * (float)delta;

            UpdateMarkers();
            UpdateCamera();
        }
    }

    private void HandleEvent(PbServerEvent ev)
    {
        switch (ev.EventCase)
        {
            case PbServerEvent.EventOneofCase.JoinAck:
                _net.SetLocalPlayerId(ev.JoinAck.PlayerId);
                break;
            case PbServerEvent.EventOneofCase.GameState:
                ApplyGameState(ev.GameState);
                break;
            case PbServerEvent.EventOneofCase.ChatMessage:
                GD.Print($"[Chat] {ev.ChatMessage.PlayerId}: {ev.ChatMessage.Message}");
                break;
        }
    }

    private void ApplyGameState(PbGameState state)
    {
        var alive = new HashSet<string>();
        foreach (var p in state.Players)
        {
            alive.Add(p.PlayerId);
            var pos = new Vector3(p.Position.X, p.Position.Y, p.Position.Z);
            var isLocal = p.PlayerId == _net.LocalPlayerId;

            if (isLocal)
            {
                _prevServerPos = _hasServerPos ? _curServerPos : pos;
                _curServerPos = pos;
                if (!_hasServerPos)
                {
                    _curLocalPos = pos;
                    _prevLocalPos = pos;
                    _localLabel.Text = $"me:{ShortId(p.PlayerId)}";
                    _hasServerPos = true;
                }
                UpdateMarkers();
                UpdateCamera();
            }
            else
            {
                if (!_remotePlayers.TryGetValue(p.PlayerId, out var node))
                    node = SpawnRemoteCapsule(p.PlayerId);
                node.Position = pos;
                node.Rotation = new Vector3(0f, p.CameraOrientation?.Yaw ?? 0f, 0f);
            }
        }

        foreach (var id in _remotePlayers.Keys.Where(k => !alive.Contains(k)).ToList())
        {
            _remotePlayers[id].QueueFree();
            _remotePlayers.Remove(id);
        }

        _hudPlayers.Text = $"players: {state.Players.Count}";
    }

    private void UpdateMarkers()
    {
        _markerCurLocal.Position   = _curLocalPos;
        _markerPrevLocal.Position  = _prevLocalPos;
        _markerCurServer.Position  = _curServerPos;
        _markerPrevServer.Position = _prevServerPos;
    }

    private void UpdateCamera()
    {
        var pivot = _curServerPos + new Vector3(0f, CameraHeight, 0f);
        var rot = Basis.FromEuler(new Vector3(_pitch, _yaw, 0f));
        var offset = rot * new Vector3(0f, 0f, CameraDistance);
        _camera.Position = pivot + offset;
        _camera.LookAt(pivot, Vector3.Up);
    }

    private MeshInstance3D SpawnRemoteCapsule(string id)
    {
        var color = ColorForId(id);
        var mat = new StandardMaterial3D
        {
            AlbedoColor = color,
            Roughness = 0.6f,
        };
        var mesh = new MeshInstance3D
        {
            Name = $"Remote_{id}",
            Mesh = new CapsuleMesh { Height = 1.8f, Radius = 0.4f },
            MaterialOverride = mat,
        };
        var label = CreateIdLabel(ShortId(id));
        mesh.AddChild(label);
        _playersRoot.AddChild(mesh);
        _remotePlayers[id] = mesh;
        return mesh;
    }

    private static string ShortId(string id) => id.Length <= 6 ? id : id[..6];

    private static Color ColorForId(string id)
    {
        unchecked
        {
            uint h = 2166136261u;
            foreach (var c in id) { h ^= c; h *= 16777619u; }
            var hue = (h % 360u) / 360f;
            return Color.FromHsv(hue, 0.7f, 0.95f);
        }
    }

    private static Label3D CreateIdLabel(string text)
    {
        return new Label3D
        {
            Text = text,
            Position = new Vector3(0f, 1.4f, 0f),
            Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
            NoDepthTest = true,
            FixedSize = true,
            PixelSize = 0.006f,
            FontSize = 48,
            OutlineSize = 8,
            Modulate = new Color(1f, 1f, 1f, 1f),
            OutlineModulate = new Color(0f, 0f, 0f, 1f),
        };
    }

    private MeshInstance3D CreateMarker(string name, Color color)
    {
        var mat = new StandardMaterial3D
        {
            AlbedoColor = color,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        };
        var mesh = new MeshInstance3D
        {
            Name = name,
            Mesh = new CapsuleMesh { Height = 1.8f, Radius = 0.4f },
            MaterialOverride = mat,
        };
        _debugRoot.AddChild(mesh);
        return mesh;
    }
}
