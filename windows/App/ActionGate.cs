namespace App;

// One user action at a time across the window and the tray menu, including
// while a confirmation dialog is open. UI-thread only.
public sealed class ActionGate
{
    public bool Busy { get; private set; }
    public event Action? Changed;

    public bool TryEnter()
    {
        if (Busy) return false;
        Busy = true;
        Changed?.Invoke();
        return true;
    }

    public void Exit()
    {
        Busy = false;
        Changed?.Invoke();
    }
}
