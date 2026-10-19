using System;
using System.Collections.Concurrent;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Grpc.Core;
using Grpc.Net.Client;
using PbClientEvent = Game.ClientEvent;
using PbServerEvent = Game.ServerEvent;
using PbJoin = Game.Join;
using PbPlayerInput = Game.PlayerInput;
using PbVector3 = Game.Vector3;
using PbOrientation = Game.Orientation;
using PbGameService = Game.GameService;

namespace BuildNDestroy.Network;

public partial class NetworkClient : Node
{
	[Export] public string ServerAddress { get; set; } = "http://192.168.137.129:50051";

	private GrpcChannel? _channel;
	private AsyncDuplexStreamingCall<PbClientEvent, PbServerEvent>? _call;
	private CancellationTokenSource? _cts;

	private readonly BlockingCollection<PbClientEvent> _outbound =
		new(new ConcurrentQueue<PbClientEvent>());
	private readonly ConcurrentQueue<PbServerEvent> _inbound = new();

	public string? LocalPlayerId { get; private set; }
	public bool IsJoined => LocalPlayerId != null;
	public bool IsConnected => _call != null;

	[Signal] public delegate void DisconnectedEventHandler(string reason);

	static NetworkClient()
	{
		AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);
	}

	public override void _Ready()
	{
		_cts = new CancellationTokenSource();
		_ = RunAsync(_cts.Token);
	}

	public override void _ExitTree()
	{
		_cts?.Cancel();
		_outbound.CompleteAdding();
		try { _call?.Dispose(); } catch { }
		try { _channel?.Dispose(); } catch { }
	}

	private async Task RunAsync(CancellationToken ct)
	{
		try
		{
			GD.Print($"[Net] connecting to {ServerAddress}");

			var httpHandler = new SocketsHttpHandler
			{
				UseProxy = false,
				EnableMultipleHttp2Connections = true,
				PooledConnectionIdleTimeout = Timeout.InfiniteTimeSpan,
				KeepAlivePingDelay = TimeSpan.FromSeconds(60),
				KeepAlivePingTimeout = TimeSpan.FromSeconds(30),
			};

			_channel = GrpcChannel.ForAddress(ServerAddress, new GrpcChannelOptions
			{
				HttpHandler = httpHandler,
				DisposeHttpClient = true,
			});

			// Одна попытка переподключения: если первый stream упал с Unauthenticated,
			// пробуем освежить access-токен через refresh и переоткрыть.
			var reconnectAllowed = true;
			while (!ct.IsCancellationRequested)
			{
				var ok = await OpenStreamAsync(ct);
				if (ok) break;
				if (!reconnectAllowed) break;
				reconnectAllowed = false;

				try
				{
					using var auth = new AuthClient(ServerAddress);
					await auth.RefreshAsync(ct);
					GD.Print("[Net] token refreshed, reconnecting");
				}
				catch (Exception e)
				{
					GD.PrintErr($"[Net] refresh failed: {e.Message}");
					AuthSession.Clear();
					EmitSignal(SignalName.Disconnected, "unauthenticated");
					return;
				}
			}
		}
		catch (OperationCanceledException) { }
		catch (Exception e)
		{
			GD.PrintErr($"[Net] fatal: {e}");
		}
	}

	// Возвращает true, если стрим корректно завершился (не из-за Unauthenticated).
	// false → надо попробовать refresh + reconnect.
	private async Task<bool> OpenStreamAsync(CancellationToken ct)
	{
		if (_channel == null) return true;
		if (string.IsNullOrEmpty(AuthSession.AccessToken))
		{
			GD.PrintErr("[Net] no access token");
			return true;
		}

		var client = new PbGameService.GameServiceClient(_channel);
		var md = new Metadata { { "authorization", $"Bearer {AuthSession.AccessToken}" } };

		try { _call?.Dispose(); } catch { }
		_call = client.Play(headers: md, cancellationToken: ct);

		try
		{
			await _call.RequestStream.WriteAsync(new PbClientEvent { Join = new PbJoin() });

			var send = Task.Run(() => SendLoop(ct), ct);
			var recv = Task.Run(() => RecvLoop(ct), ct);
			await Task.WhenAny(send, recv);
			return true;
		}
		catch (RpcException ex) when (ex.StatusCode == StatusCode.Unauthenticated)
		{
			GD.Print("[Net] stream unauthenticated, will try refresh");
			return false;
		}
		catch (OperationCanceledException) { return true; }
		catch (Exception e)
		{
			GD.PrintErr($"[Net] stream: {e.Message}");
			return true;
		}
	}

	private async Task SendLoop(CancellationToken ct)
	{
		try
		{
			foreach (var ev in _outbound.GetConsumingEnumerable(ct))
			{
				if (_call == null) return;
				await _call.RequestStream.WriteAsync(ev);
			}
			if (_call != null) await _call.RequestStream.CompleteAsync();
		}
		catch (OperationCanceledException) { }
		catch (RpcException ex) when (ex.StatusCode == StatusCode.Unauthenticated) { throw; }
		catch (Exception e) { GD.PrintErr($"[Net] send: {e.Message}"); }
	}

	private async Task RecvLoop(CancellationToken ct)
	{
		try
		{
			if (_call == null) return;
			while (await _call.ResponseStream.MoveNext(ct))
			{
				_inbound.Enqueue(_call.ResponseStream.Current);
			}
		}
		catch (OperationCanceledException) { }
		catch (RpcException ex) when (ex.StatusCode == StatusCode.Unauthenticated) { throw; }
		catch (Exception e) { GD.PrintErr($"[Net] recv: {e.Message}"); }
	}

	public void SendInput(Vector3 moveDir, float yaw, float pitch)
	{
		if (!IsJoined) return;
		if (_outbound.IsAddingCompleted) return;

		var ev = new PbClientEvent
		{
			PlayerInput = new PbPlayerInput
			{
				MovementDirection = new PbVector3 { X = moveDir.X, Y = moveDir.Y, Z = moveDir.Z },
				CameraOrientation = new PbOrientation { Yaw = yaw, Pitch = pitch },
			},
		};
		try { _outbound.Add(ev); } catch (InvalidOperationException) { }
	}

	public bool TryDequeue(out PbServerEvent? ev) => _inbound.TryDequeue(out ev);

	internal void SetLocalPlayerId(string id)
	{
		LocalPlayerId = id;
		GD.Print($"[Net] joined as {id}");
	}
}
