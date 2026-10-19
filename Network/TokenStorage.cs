using System;
using System.Text.Json;
using Godot;

namespace BuildNDestroy.Network;

// Хранение access/refresh между запусками в user://auth.json.
// В Windows это %APPDATA%/Godot/app_userdata/<project>/auth.json.
// В проде лучше шифровать (DPAPI на Windows), пока держим как есть.
public static class TokenStorage
{
	private const string Path = "user://auth.json";

	private sealed class Payload
	{
		public string? Access { get; set; }
		public string? Refresh { get; set; }
		public long AccessExp { get; set; }
		public long RefreshExp { get; set; }
	}

	public static void Save(string access, string refresh, long accessExp, long refreshExp)
	{
		try
		{
			var json = JsonSerializer.Serialize(new Payload
			{
				Access = access,
				Refresh = refresh,
				AccessExp = accessExp,
				RefreshExp = refreshExp,
			});
			using var f = FileAccess.Open(Path, FileAccess.ModeFlags.Write);
			if (f == null) return;
			f.StoreString(json);
		}
		catch (Exception e) { GD.PrintErr($"[Auth] save failed: {e.Message}"); }
	}

	public static bool Load(out string access, out string refresh, out long accessExp, out long refreshExp)
	{
		access = refresh = "";
		accessExp = refreshExp = 0;
		try
		{
			if (!FileAccess.FileExists(Path)) return false;
			using var f = FileAccess.Open(Path, FileAccess.ModeFlags.Read);
			if (f == null) return false;
			var json = f.GetAsText();
			var p = JsonSerializer.Deserialize<Payload>(json);
			if (p == null || string.IsNullOrEmpty(p.Refresh)) return false;
			access = p.Access ?? "";
			refresh = p.Refresh;
			accessExp = p.AccessExp;
			refreshExp = p.RefreshExp;
			return true;
		}
		catch (Exception e)
		{
			GD.PrintErr($"[Auth] load failed: {e.Message}");
			return false;
		}
	}

	public static void Clear()
	{
		try
		{
			if (FileAccess.FileExists(Path))
				DirAccess.RemoveAbsolute(ProjectSettings.GlobalizePath(Path));
		}
		catch (Exception e) { GD.PrintErr($"[Auth] clear failed: {e.Message}"); }
	}
}
