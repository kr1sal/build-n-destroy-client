using System;
using BuildNDestroy.Network;
using Godot;
using Grpc.Core;

namespace BuildNDestroy.UI;

public partial class LoginScreen : Control
{
	[Export] public string ServerAddress { get; set; } = "http://127.0.0.1:50051";
	[Export] public string NextScene { get; set; } = "res://Main.tscn";

	private LineEdit _login = null!;
	private LineEdit _password = null!;
	private Button _submit = null!;
	private Button _switchMode = null!;
	private Label _error = null!;

	private bool _registerMode;

	public override void _Ready()
	{
		_login = GetNode<LineEdit>("%LoginEdit");
		_password = GetNode<LineEdit>("%PasswordEdit");
		_submit = GetNode<Button>("%SubmitButton");
		_switchMode = GetNode<Button>("%SwitchModeButton");
		_error = GetNode<Label>("%ErrorLabel");

		_submit.Pressed += OnSubmit;
		_switchMode.Pressed += OnSwitchMode;
		_password.TextSubmitted += _ => OnSubmit();
		_login.TextSubmitted += _ => _password.GrabFocus();

		ApplyMode();
		_login.GrabFocus();

		// Авто-логин: если есть валидный refresh — тихо обновляемся и уходим в игру.
		if (AuthSession.LoadPersisted() && AuthSession.HasRefresh)
			_ = TryAutoLogin();
	}

	private async System.Threading.Tasks.Task TryAutoLogin()
	{
		SetBusy(true);
		try
		{
			using var auth = new AuthClient(ServerAddress);
			await auth.RefreshAsync();
			GetTree().ChangeSceneToFile(NextScene);
		}
		catch (RpcException ex)
		{
			AuthSession.Clear();
			GD.Print($"[Auth] auto-login failed: {ex.StatusCode}");
		}
		catch (Exception ex)
		{
			GD.PrintErr($"[Auth] auto-login error: {ex.Message}");
		}
		finally { SetBusy(false); }
	}

	private void OnSwitchMode()
	{
		_registerMode = !_registerMode;
		ShowError("");
		ApplyMode();
	}

	private void ApplyMode()
	{
		if (_registerMode)
		{
			_submit.Text = "Create account";
			_switchMode.Text = "Already have an account? Sign in";
		}
		else
		{
			_submit.Text = "Sign in";
			_switchMode.Text = "Don't have an account? Register";
		}
	}

	private async void OnSubmit()
	{
		var login = _login.Text.Trim();
		var password = _password.Text;
		if (string.IsNullOrEmpty(login) || string.IsNullOrEmpty(password))
		{
			ShowError("Enter login and password");
			return;
		}
		if (_registerMode && password.Length < 8)
		{
			ShowError("Password must be at least 8 characters");
			return;
		}

		SetBusy(true);
		ShowError("");

		try
		{
			using var auth = new AuthClient(ServerAddress);
			if (_registerMode)
				await auth.RegisterAsync(login, password);
			else
				await auth.LoginAsync(login, password);

			GetTree().ChangeSceneToFile(NextScene);
		}
		catch (RpcException ex)
		{
			ShowError(ex.StatusCode switch
			{
				StatusCode.Unauthenticated => "Invalid credentials",
				StatusCode.AlreadyExists   => "Login already taken",
				StatusCode.InvalidArgument => ex.Status.Detail,
				StatusCode.Unavailable     => $"Server unavailable: {ex.Status.Detail}",
				_                          => $"{ex.StatusCode}: {ex.Status.Detail}",
			});
		}
		catch (Exception ex)
		{
			ShowError(ex.Message);
		}
		finally
		{
			SetBusy(false);
		}
	}

	private void SetBusy(bool busy)
	{
		_submit.Disabled = busy;
		_switchMode.Disabled = busy;
		_login.Editable = !busy;
		_password.Editable = !busy;
		if (busy) _submit.Text = "...";
		else ApplyMode();
	}

	private void ShowError(string msg)
	{
		_error.Text = msg;
		_error.Visible = !string.IsNullOrEmpty(msg);
	}
}
