using TerraFX.Interop.Windows;
using static TerraFX.Interop.Windows.Windows;

namespace HyperPlot.Wpf.Tests;

/// <summary>
/// The key state of the calling thread (<c>GetKeyboardState</c>, <c>SetKeyboardState</c>), which is where WPF's
/// <c>Keyboard.Modifiers</c> comes from on that thread: a run of the tests with Shift, Ctrl, or Alt marked as down in it read
/// that modifier in every test (Issue #564, .NET 10). The view passes <c>Keyboard.Modifiers</c> with each input, and the
/// default bindings of a move, a drag, and the wheel take no modifier, so a key that reads as down keeps the input from its
/// tool. Nothing here reaches another thread or process.
/// </summary>
internal static unsafe class ThreadKeys
{
    // The length of the table GetKeyboardState fills: one byte for each virtual key.
    private const int Keys = 256;

    // The high bit of a key's byte says that the key is down.
    private const byte Down = 0x80;

    /// <summary>Gets the virtual keys of Shift, Ctrl, and Alt: each one's own and its left and right.</summary>
    public static ReadOnlySpan<int> Modifiers =>
        [VK.VK_SHIFT, VK.VK_LSHIFT, VK.VK_RSHIFT, VK.VK_CONTROL, VK.VK_LCONTROL, VK.VK_RCONTROL, VK.VK_MENU, VK.VK_LMENU, VK.VK_RMENU];

    /// <summary>Marks Shift, Ctrl, and Alt as up in the calling thread's key state, whatever the keyboard says.</summary>
    public static void ReleaseModifiers() => Set(Modifiers, down: false);

    /// <summary>Marks virtual keys as down in the calling thread's key state, as a key held when the thread last read it.</summary>
    public static void Hold(ReadOnlySpan<int> keys) => Set(keys, down: true);

    private static void Set(ReadOnlySpan<int> keys, bool down)
    {
        // Both calls take a table of 256 bytes: this one is that long and outlives them, on this stack frame.
        var table = stackalloc byte[Keys];
        if (!GetKeyboardState(table))
        {
            throw new InvalidOperationException("GetKeyboardState failed for the calling thread.");
        }

        var states = new Span<byte>(table, Keys);
        var changed = false;
        foreach (var key in keys)
        {
            var state = down ? (byte)(states[key] | Down) : (byte)(states[key] & ~Down);
            changed |= state != states[key];
            states[key] = state;
        }

        if (changed && !SetKeyboardState(table))
        {
            throw new InvalidOperationException("SetKeyboardState failed for the calling thread.");
        }
    }
}
