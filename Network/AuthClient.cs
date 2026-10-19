using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Core;
using Grpc.Net.Client;
using Auth;

namespace BuildNDestroy.Network;

public sealed class AuthClient : IDisposable
{
	private readonly GrpcChannel _channel;
	private readonly AuthService.AuthServiceClient _client;

	static AuthClient()
	{
		AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);
	}

	public AuthClient(string serverAddress)
	{
		var http = new SocketsHttpHandler
		{
			UseProxy = false,
			EnableMultipleHttp2Connections = true,
		};
		_channel = GrpcChannel.ForAddress(serverAddress, new GrpcChannelOptions
		{
			HttpHandler = http,
			DisposeHttpClient = true,
		});
		_client = new AuthService.AuthServiceClient(_channel);
	}

	public async Task<TokenPair> LoginAsync(string login, string password, CancellationToken ct = default)
	{
		var pair = await _client.LoginAsync(
			new LoginRequest { Login = login, Password = password },
			cancellationToken: ct);
		AuthSession.Set(pair.AccessToken, pair.RefreshToken, pair.AccessExpiresAt, pair.RefreshExpiresAt);
		return pair;
	}

	public async Task<TokenPair> RegisterAsync(string login, string password, CancellationToken ct = default)
	{
		var pair = await _client.RegisterAsync(
			new RegisterRequest { Login = login, Password = password },
			cancellationToken: ct);
		AuthSession.Set(pair.AccessToken, pair.RefreshToken, pair.AccessExpiresAt, pair.RefreshExpiresAt);
		return pair;
	}

	public async Task<TokenPair> RefreshAsync(CancellationToken ct = default)
	{
		if (string.IsNullOrEmpty(AuthSession.RefreshToken))
			throw new InvalidOperationException("no refresh token");
		var pair = await _client.RefreshAsync(
			new RefreshRequest { RefreshToken = AuthSession.RefreshToken },
			cancellationToken: ct);
		AuthSession.Set(pair.AccessToken, pair.RefreshToken, pair.AccessExpiresAt, pair.RefreshExpiresAt);
		return pair;
	}

	public async Task LogoutAsync(CancellationToken ct = default)
	{
		var refresh = AuthSession.RefreshToken;
		AuthSession.Clear();
		if (!string.IsNullOrEmpty(refresh))
		{
			try
			{
				await _client.LogoutAsync(new LogoutRequest { RefreshToken = refresh }, cancellationToken: ct);
			}
			catch (RpcException) { /* idempotent */ }
		}
	}

	public void Dispose()
	{
		try { _channel.Dispose(); } catch { }
	}
}
