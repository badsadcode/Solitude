using System;
using Godot;
using Solitude.Core;

namespace Solitude.Interior;

/// <summary>
/// Anything the player can look at and press E on. The player's interaction ray hits the
/// collision shape; Prompt goes to the HUD; Use() fires the action.
/// </summary>
public partial class Interactable : StaticBody3D
{
    public string Prompt = "Use";
    public Action OnUse;

    public Interactable()
    {
        CollisionLayer = Layers.Interior;
        CollisionMask = 0;
    }

    public static Interactable Create(Node3D parent, Vector3 center, Vector3 size, string prompt, Action onUse)
    {
        var it = new Interactable { Prompt = prompt, OnUse = onUse, Position = center };
        it.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = size } });
        parent.AddChild(it);
        return it;
    }

    public void Use() => OnUse?.Invoke();
}
