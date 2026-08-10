using Skua.Core.Interfaces;
using Skua.Core.Models;

namespace Skua.Backend.Linux.Services;

public sealed class LinuxDialogService : IDialogService
{
    public bool? ShowDialog<TViewModel>(
        TViewModel viewModel
    )
        where TViewModel : class
    {
        Console.WriteLine(
            $"[Diálogo Linux] {typeof(TViewModel).Name}"
        );

        return null;
    }

    public bool? ShowDialog<TViewModel>(
        TViewModel viewModel,
        string Title
    )
        where TViewModel : class
    {
        Console.WriteLine(
            $"[Diálogo Linux] {Title}"
        );

        return null;
    }

    public bool? ShowDialog<TViewModel>(
        TViewModel viewModel,
        Action<TViewModel> callback
    )
        where TViewModel : class
    {
        callback(viewModel);
        return null;
    }

    public void ShowMessageBox(
        string message,
        string caption
    )
    {
        Console.WriteLine(
            $"[{caption}] {message}"
        );
    }

    public bool? ShowMessageBox(
        string message,
        string caption,
        bool yesAndNo
    )
    {
        Console.WriteLine(
            $"[{caption}] {message}"
        );

        return null;
    }

    public DialogResult ShowMessageBox(
        string message,
        string caption,
        params string[] buttons
    )
    {
        Console.WriteLine(
            $"[{caption}] {message}"
        );

        return default;
    }
}
