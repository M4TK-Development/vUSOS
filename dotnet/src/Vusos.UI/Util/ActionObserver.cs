namespace Vusos.UI.Util;

/// <summary>Minimalny IObserver na delegacie – żeby nie ciągnąć System.Reactive do UI.</summary>
public sealed class ActionObserver<T>(Action<T> onNext) : IObserver<T>
{
    public void OnCompleted() { }
    public void OnError(Exception error) { }
    public void OnNext(T value) => onNext(value);
}
