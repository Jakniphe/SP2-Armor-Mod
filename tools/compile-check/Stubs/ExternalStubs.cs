using UnityEngine;

// Stand-ins for the packages the decompiled game does not contain (their real DLLs ship with the game): Unity's Input
// System and UI, and FishNet. Only what the mod uses.

namespace UnityEngine.InputSystem
{
    public enum Key { None = 0, F1 = 94, F2 = 95, F3 = 96, F4 = 97, F5 = 98, F6 = 99, F7 = 100, F8 = 101, F9 = 102, F10 = 103, F11 = 104, F12 = 105, Insert = 1 }

    public class KeyControl
    {
        public bool wasPressedThisFrame { get { return false; } }
        public bool isPressed { get { return false; } }
    }

    public class Keyboard
    {
        public static Keyboard current { get { return null; } }
        public KeyControl this[Key key] { get { return null; } }
    }
}

namespace UnityEngine.UI
{
    public abstract class Graphic : MonoBehaviour
    {
        public Color color { get; set; }
        public bool raycastTarget { get; set; }
    }

    public class Image : Graphic
    {
        public Sprite sprite { get; set; }
    }

    public class RawImage : Graphic
    {
        public Texture texture { get; set; }
        public Rect uvRect { get; set; }
    }
}

namespace FishNet
{
    public static class InstanceFinder
    {
        public static FishNet.Managing.NetworkManager NetworkManager { get { return null; } }
    }
}

namespace FishNet.Managing
{
    public class NetworkManager : MonoBehaviour { }
}

namespace FishNet.Object
{
    // The game's networked scripts derive from this: what they inherit (and the mod reads) is listed here.
    public abstract class NetworkBehaviour : MonoBehaviour
    {
        public bool IsServerStarted { get { return false; } }
        public bool IsClientStarted { get { return false; } }
        public bool IsOwner { get { return false; } }
        public int OwnerId { get { return 0; } }
    }
}

namespace FishNet.Serializing
{
    public class Writer
    {
        public void WriteString(string value) { }
    }

    public class PooledWriter : Writer { }

    public class Reader
    {
        public byte ReadUInt8Unpacked() { return 0; }
        public ushort ReadUInt16() { return 0; }
        public string ReadStringAllocated() { return null; }
    }

    public class PooledReader : Reader
    {
        public void Store() { }
    }

    public static class ReaderPool
    {
        public static PooledReader Retrieve(System.ArraySegment<byte> bytes, FishNet.Managing.NetworkManager manager) { return null; }
    }
}
