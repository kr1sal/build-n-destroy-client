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
	[Export] public float ThirdPersonDistance { get; set; } = 4.0f;
	[Export] public float ThirdPersonHeight { get; set; } = 0.75f;

	[Export] public bool DebugCollisions { get; set; } = false;
	[Export] public float PredictedMoveSpeed { get; set; } = 5.0f;

	private const float CapsuleHeight = 1.8f;
	private const float CapsuleRadius = 0.4f;

	private NetworkClient _net = null!;
	private Camera3D _camera = null!;
	private Node3D _playersRoot = null!;
	private Node3D _debugRoot = null!;

	private readonly Dictionary<string, MeshInstance3D> _players = new();
	private float _yaw;
	private float _pitch;
	private bool _thirdPerson;

	private bool _wantsMouseCaptured = true;
	private bool _hasLocalState;
	private Vector3 _serverPos;
	private Vector3 _prevServerPos;
	private Vector3 _predictedPos;
	private Vector3 _prevPredictedPos;

	private MeshInstance3D _dbgServerCur = null!;
	private MeshInstance3D _dbgServerPrev = null!;
	private MeshInstance3D _dbgClientCur = null!;
	private MeshInstance3D _dbgClientPrev = null!;

	public override void _Ready()
	{
		_net = GetNode<NetworkClient>("NetworkClient");
		_camera = GetNode<Camera3D>("Camera3D");
		_playersRoot = GetNode<Node3D>("Players");

		_debugRoot = new Node3D { Name = "DebugCollisions", Visible = DebugCollisions };
		AddChild(_debugRoot);

		// Server — red hues; Client — cyan/blue hues. Previous = dimmer + more transparent.
		_dbgServerCur = MakeDebugCapsule("DbgServerCur", new Color(1.0f, 0.25f, 0.25f, 0.55f));
		_dbgServerPrev = MakeDebugCapsule("DbgServerPrev", new Color(0.55f, 0.10f, 0.10f, 0.30f));
		_dbgClientCur = MakeDebugCapsule("DbgClientCur", new Color(0.25f, 0.75f, 1.00f, 0.55f));
		_dbgClientPrev = MakeDebugCapsule("DbgClientPrev", new Color(0.10f, 0.35f, 0.60f, 0.30f));

		Input.MouseMode = Input.MouseModeEnum.Captured;
	}

	public override void _Notification(int what)
	{
		if (what == NotificationApplicationFocusIn || what == NotificationWMWindowFocusIn)
		{
			if (_wantsMouseCaptured)
				Input.MouseMode = Input.MouseModeEnum.Captured;
		}
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
			{
				_wantsMouseCaptured = false;
				Input.MouseMode = Input.MouseModeEnum.Visible;
			}
			else if (key.Keycode == Key.F1)
			{
				_wantsMouseCaptured = true;
				Input.MouseMode = Input.MouseModeEnum.Captured;
			}
			else if (key.Keycode == Key.V)
				_thirdPerson = !_thirdPerson;
			else if (key.Keycode == Key.F3)
			{
				DebugCollisions = !DebugCollisions;
				_debugRoot.Visible = DebugCollisions;
			}
		}
	}

	public override void _PhysicsProcess(double delta)
	{
		while (_net.TryDequeue(out var ev) && ev != null)
			HandleEvent(ev);

		if (!_net.IsConnected) return;

		var local = Vector3.Zero;
		if (Input.IsKeyPressed(Key.W)) local += Vector3.Forward;
		if (Input.IsKeyPressed(Key.S)) local += Vector3.Back;
		if (Input.IsKeyPressed(Key.A)) local += Vector3.Left;
		if (Input.IsKeyPressed(Key.D)) local += Vector3.Right;
		if (local.LengthSquared() > 0f) local = local.Normalized();

		var yawBasis = Basis.FromEuler(new Vector3(0f, _yaw, 0f));
		var worldMove = yawBasis * local;

		_net.SendInput(worldMove, _yaw, _pitch);

		if (_hasLocalState)
		{
			_prevPredictedPos = _predictedPos;
			_predictedPos += worldMove * PredictedMoveSpeed * (float)delta;
			UpdateDebugVisuals();
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
			if (!_players.TryGetValue(p.PlayerId, out var node))
				node = SpawnCapsule(p.PlayerId);

			var pos = new Vector3(p.Position.X, p.Position.Y, p.Position.Z);
			node.Position = pos;

			var isLocal = p.PlayerId == _net.LocalPlayerId;

			if (isLocal)
			{
				node.Visible = _thirdPerson;
				node.Rotation = new Vector3(0f, _yaw, 0f);

				var head = pos + new Vector3(0f, ThirdPersonHeight, 0f);
				if (_thirdPerson)
				{
					var basis = Basis.FromEuler(new Vector3(_pitch, _yaw, 0f));
					_camera.Position = head + basis * new Vector3(0f, 0f, ThirdPersonDistance);
				}
				else
				{
					_camera.Position = head;
				}
				_camera.Rotation = new Vector3(_pitch, _yaw, 0f);

				_prevServerPos = _hasLocalState ? _serverPos : pos;
				_serverPos = pos;
				if (!_hasLocalState)
				{
					_predictedPos = pos;
					_prevPredictedPos = pos;
					_hasLocalState = true;
				}
			}
			else
			{
				node.Visible = true;
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
			Mesh = new CapsuleMesh { Height = CapsuleHeight, Radius = CapsuleRadius },
		};
		_playersRoot.AddChild(mesh);
		_players[id] = mesh;
		return mesh;
	}

	private MeshInstance3D MakeDebugCapsule(string name, Color color)
	{
		var mat = new StandardMaterial3D
		{
			AlbedoColor = color,
			Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
			ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
			CullMode = BaseMaterial3D.CullModeEnum.Disabled,
			NoDepthTest = true,
		};
		var mesh = new MeshInstance3D
		{
			Name = name,
			Mesh = new CapsuleMesh { Height = CapsuleHeight, Radius = CapsuleRadius },
			MaterialOverride = mat,
			CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
			Visible = false,
		};
		_debugRoot.AddChild(mesh);
		return mesh;
	}

	private void UpdateDebugVisuals()
	{
		if (!DebugCollisions) return;

		_dbgServerCur.Visible = true;
		_dbgServerPrev.Visible = true;
		_dbgClientCur.Visible = true;
		_dbgClientPrev.Visible = true;

		_dbgServerCur.Position = _serverPos;
		_dbgServerPrev.Position = _prevServerPos;
		_dbgClientCur.Position = _predictedPos;
		_dbgClientPrev.Position = _prevPredictedPos;
	}
}
