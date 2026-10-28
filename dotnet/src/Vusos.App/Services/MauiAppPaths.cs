using Microsoft.Maui.Storage;
using Vusos.Core.Platform;

namespace Vusos.App.Services;

/// <summary>Prywatne katalogi aplikacji (sandbox): baza SQLite i pobrane materiały.</summary>
public sealed class MauiAppPaths : IAppPaths
{
    public string DataDirectory { get; } = FileSystem.AppDataDirectory;

    public string MaterialsDirectory { get; } = Path.Combine(FileSystem.AppDataDirectory, "materials");
}
