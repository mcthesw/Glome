using Godot;

namespace Glome;

// The local +Y face supports a walker. Its other faces remain solid, including
// the soffit of an elevated bridge. A sheared box describes a flight of stairs.
[Tool, GlobalClass]
public partial class WalkableBoxShape3D : BoxShape3D { }
