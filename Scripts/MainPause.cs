using Godot;
using Solitude.Systems;
using Solitude.UI;

namespace Solitude;

/// <summary>The in-dive pause menu: freezes the world, keeps the menu alive.</summary>
public partial class Main
{
    private PauseMenu _pause;
    private Input.MouseModeEnum _mouseBeforePause;

    private void SetupPause()
    {
        _pause = new PauseMenu
        {
            Name = "Pause",
            OnResume = ClosePause,
            OnQuit = () => { Autosave("quit"); GetTree().Quit(); },
            OnSaveAndTitle = () =>
            {
                Autosave("menu");
                GetTree().Paused = false;
                GetTree().ReloadCurrentScene();
            },
        };
        AddChild(_pause);
    }

    private void OpenPause()
    {
        _mouseBeforePause = Input.MouseMode;
        Input.MouseMode = Input.MouseModeEnum.Visible;
        GetTree().Paused = true;
        _pause.Open();
    }

    private void ClosePause()
    {
        _pause.Close();
        GetTree().Paused = false;
        Input.MouseMode = _mouseBeforePause;
    }
}
