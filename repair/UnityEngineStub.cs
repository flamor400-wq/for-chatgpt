namespace UnityEngine
{
    // Compile-time facade only. This is not copied beside the plugin.
    // The real UnityEngine.CoreModule is supplied by R.E.P.O. at runtime.
    public class Object { }
    public class Component : Object { }
    public class Behaviour : Component { }
    public class MonoBehaviour : Behaviour { }
}
