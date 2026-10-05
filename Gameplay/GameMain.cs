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

    private NetworkClient _net = null!;
    private Camera3D _camera = null!;
    private Node3D _playersRoot = null!;

    private readonly Dictionary<string, MeshInstance3D> _players = new();
    private float _yaw;
    private float _pitch;

    public override void _Ready()
    {
        _net = GetNode<NetworkClient>("NetworkClient");
        _camera = GetNode<Camera3D>("Camera3D");
        _playersRoot = GetNode<Node3D>("Players");
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

        if (@event is InputEventKey key && key.Pressed && !key.Echo)
        {
            if (key.Keycode == Key.Escape)
                Input.MouseMode = Input.MouseModeEnum.Visible;
            else if (key.Keycode == Key.F1)
                Input.MouseMode = Input.MouseModeEnum.Captured;
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

        _net.SendInput(worldMove, _yaw, _pitch);
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
            if (!_players.TryGetValue(p.PlayerId, out var node))
                node = SpawnCapsule(p.PlayerId);

            var pos = new Vector3(p.Position.X, p.Position.Y, p.Position.Z);
            node.Position = pos;

            var isLocal = p.PlayerId == _net.LocalPlayerId;
            node.Visible = !isLocal;

            if (isLocal)
            {
                _camera.Position = pos + new Vector3(0f, 0.75f, 0f);
                _camera.Rotation = new Vector3(_pitch, _yaw, 0f);
            }
            else
            {
                node.Rotation = new Vector3(0f, p.CameraOrientation?.Yaw ?? 0f, 0f);
            }
        }

        foreach (var id in _players.Keys.Where(k => !alive.Contains(k)).ToList())
        {
            _players[id].QueueFree();
            _players.Remove(id);
        }
    }

    private MeshInstance3D SpawnCapsule(string id)
    {
        var mesh = new MeshInstance3D
        {
            Name = $"Player_{id}",
            Mesh = new CapsuleMesh { Height = 1.8f, Radius = 0.4f },
        };
        _playersRoot.AddChild(mesh);
        _players[id] = mesh;
        return mesh;
    }
}
