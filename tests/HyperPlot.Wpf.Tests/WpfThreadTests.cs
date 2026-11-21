using System.Windows.Input;
using TerraFX.Interop.Windows;

namespace HyperPlot.Wpf.Tests;

/// <summary>
/// <see cref="WpfThread"/>: what its <see cref="WpfThread.Run(Action)"/> gives the work of a test. The class shares the WPF
/// thread, so it runs in the collection of the classes that run alone (rule testing).
/// </summary>
[Collection(WpfThread.Collection)]
public sealed class WpfThreadTests(WpfThread wpf)
{
    /// <summary>
    /// Work on the WPF thread starts with no modifier key down, whatever the thread's key state held before (Issue #564): a
    /// key someone holds while the tests run does not reach the input a test sends.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The assembly's WPF thread. The cases are Shift, Ctrl, and Alt, each as its own virtual key with its left and right.
    ///
    /// Steps:
    /// - In one piece of work, mark the case's keys as down in the thread's key state and read <c>Keyboard.Modifiers</c>;
    ///   in the next piece of work, read it again.
    ///
    /// Expected:
    /// - The first read is the case's modifier: the thread's key state is what WPF reads. The second read is None, which is
    ///   the assertion that guards the fix; without the release in <c>Run</c> it is the case's modifier.
    /// </remarks>
    [Theory]
    [InlineData(ModifierKeys.Shift)]
    [InlineData(ModifierKeys.Control)]
    [InlineData(ModifierKeys.Alt)]
    public void Run_ModifierDownInTheThreadsKeyState_StartsTheWorkWithNone(ModifierKeys modifier)
    {
        try
        {
            var held = wpf.Run(() =>
            {
                ThreadKeys.Hold(
                    modifier switch
                    {
                        ModifierKeys.Shift => [VK.VK_SHIFT, VK.VK_LSHIFT, VK.VK_RSHIFT],
                        ModifierKeys.Control => [VK.VK_CONTROL, VK.VK_LCONTROL, VK.VK_RCONTROL],
                        _ => [VK.VK_MENU, VK.VK_LMENU, VK.VK_RMENU],
                    }
                );
                return Keyboard.Modifiers;
            });
            var next = wpf.Run(() => Keyboard.Modifiers);

            Assert.Equal(modifier, held);
            Assert.Equal(ModifierKeys.None, next);
        }
        finally
        {
            // The tests after this one start clean even when Run does not release the keys.
            wpf.Run(ThreadKeys.ReleaseModifiers);
        }
    }
}
