namespace Vusos.UI.Services;

public enum ToastKind { Info, Success, Error }

public sealed record Toast(Guid Id, string Message, ToastKind Kind);

/// <summary>Krótkie powiadomienia (znikają po kilku sekundach).</summary>
public sealed class ToastService
{
    private readonly List<Toast> _items = [];
    private readonly object _lock = new();

    public event Action? Changed;

    public IReadOnlyList<Toast> Items
    {
        get { lock (_lock) return _items.ToArray(); }
    }

    public void Show(string message, ToastKind kind = ToastKind.Info, TimeSpan? lifetime = null)
    {
        var toast = new Toast(Guid.NewGuid(), message, kind);
        lock (_lock) _items.Add(toast);
        Changed?.Invoke();
        _ = ExpireAsync(toast, lifetime ?? TimeSpan.FromSeconds(kind == ToastKind.Error ? 8 : 4));
    }

    public void Success(string message) => Show(message, ToastKind.Success);
    public void Error(string message) => Show(message, ToastKind.Error);

    public void Dismiss(Guid id)
    {
        lock (_lock) _items.RemoveAll(t => t.Id == id);
        Changed?.Invoke();
    }

    private async Task ExpireAsync(Toast toast, TimeSpan after)
    {
        await Task.Delay(after);
        Dismiss(toast.Id);
    }
}
