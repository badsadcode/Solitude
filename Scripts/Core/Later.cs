using System;
using Godot;

namespace Solitude.Core;

/// <summary>
/// A delayed action that dies with its owner. A SceneTree timer outlives the scene, so a plain
/// <c>GetTree().CreateTimer(t).Timeout += ...</c> can fire into a freed node after CONTINUE or a
/// return to the title (it did: a wreck call after the save test's reload). Use
/// <c>Later.After(this, t).Timeout += ...</c> instead; the callback is skipped if the owner is gone.
/// </summary>
public readonly struct Later
{
    private readonly GodotObject _owner;
    private readonly SceneTreeTimer _timer;

    private Later(GodotObject owner, SceneTreeTimer timer) { _owner = owner; _timer = timer; }

    public static Later After(Node owner, double seconds) => new(owner, owner.GetTree().CreateTimer(seconds));

    public event Action Timeout
    {
        add
        {
            var owner = _owner;
            _timer.Timeout += () => { if (GodotObject.IsInstanceValid(owner)) value(); };
        }
        remove { }
    }
}
