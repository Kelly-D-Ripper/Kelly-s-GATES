namespace UrsaMinorThrottle
{
    // Give the input system one frame to receive focus, then use the live levers.
    internal sealed class FocusPickup
    {
        private int focusFrame;
        private bool focused;
        internal void Lost() { focused = false; }
        internal void Restored(int frame) { focusFrame = frame; focused = true; }
        internal bool Ready(int frame) => focused && frame > focusFrame;
    }
}
