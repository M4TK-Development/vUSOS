namespace Vusos.Core.Adapters;

/// <summary>Sesja w systemie dziedzinowym wygasła albo poświadczenia są nieprawidłowe. UI: „Połącz ponownie”.</summary>
public class AdapterAuthException(string message = "Sesja wygasła lub brak połączenia", Exception? inner = null)
    : Exception(message, inner);

/// <summary>System dziedzinowy niedostępny (przeciążenie, timeout, błąd 5xx, nieznana odpowiedź). UI: dane z cache.</summary>
public class AdapterUnavailableException(string message, Exception? inner = null) : Exception(message, inner)
{
    public bool IsTransient { get; init; } = true;
}

/// <summary>Funkcja nie istnieje w danej instalacji (np. moduł USOS API wyłączony). UI: ukryj sekcję.</summary>
public class AdapterNotSupportedException(string message) : Exception(message);

public class NotConnectedException(Domain.SourceSystem system)
    : Exception($"Brak połączenia z systemem {system}")
{
    public Domain.SourceSystem System { get; } = system;
}
