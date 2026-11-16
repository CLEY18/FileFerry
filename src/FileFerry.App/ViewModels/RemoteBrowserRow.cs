using System.Windows.Input;
using FileFerry.Core.Domain.Paths;

namespace FileFerry.App.ViewModels;

/// <summary>
/// Row of RemoteBrowserRow: one folder of the current level; it carries its own
/// open command — CollectionView templates in MAUI 10 do not resolve ancestor bindings.
/// </summary>
/// <param name="Name">Folder name.</param>
/// <param name="Path">Canonical remote path of the folder.</param>
/// <param name="OpenCommand">Row tap → enter the folder (the parameter is this very row).</param>
public sealed record RemoteBrowserRow(string Name, RemotePath Path, ICommand? OpenCommand);
