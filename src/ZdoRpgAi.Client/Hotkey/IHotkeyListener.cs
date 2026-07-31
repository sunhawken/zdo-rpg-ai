namespace ZdoRpgAi.Client.Hotkey;

public interface IHotkeyListener : IDisposable {
    event Action? KeyPressed;
    event Action? KeyReleased;

    Task RunAsync(CancellationToken ct);

    /// <summary>
    /// While suppressed, key transitions are still tracked internally but KeyPressed/KeyReleased
    /// don't fire -- used to stop this hotkey from firing while the player is typing into the
    /// in-game chat box's text field (e.g. the PTT key is the letter it's bound to, which is a
    /// very plausible thing to type).
    /// </summary>
    void SetSuppressed(bool suppressed);
}
