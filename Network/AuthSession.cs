using System;

namespace BuildNDestroy.Network;

public static class AuthSession
{
	public static string? AccessToken { get; private set; }
	public static string? RefreshToken { get; private set; }
	public static DateTimeOffset AccessExpiresAt { get; private set; }
	public static DateTimeOffset RefreshExpiresAt { get; private set; }

	public static bool HasAccess => !string.IsNullOrEmpty(AccessToken);
	public static bool HasRefresh =>
		!string.IsNullOrEmpty(RefreshToken) &&
		RefreshExpiresAt > DateTimeOffset.UtcNow;

	public static void Set(string access, string refresh, long accessExpUnix, long refreshExpUnix, bool persist = true)
	{
		AccessToken = access;
		RefreshToken = refresh;
		AccessExpiresAt = DateTimeOffset.FromUnixTimeSeconds(accessExpUnix);
		RefreshExpiresAt = DateTimeOffset.FromUnixTimeSeconds(refreshExpUnix);
		if (persist) TokenStorage.Save(access, refresh, accessExpUnix, refreshExpUnix);
	}

	public static bool LoadPersisted()
	{
		if (!TokenStorage.Load(out var a, out var r, out var ae, out var re)) return false;
		AccessToken = a;
		RefreshToken = r;
		AccessExpiresAt = DateTimeOffset.FromUnixTimeSeconds(ae);
		RefreshExpiresAt = DateTimeOffset.FromUnixTimeSeconds(re);
		return true;
	}

	public static void Clear()
	{
		AccessToken = null;
		RefreshToken = null;
		AccessExpiresAt = default;
		RefreshExpiresAt = default;
		TokenStorage.Clear();
	}
}
