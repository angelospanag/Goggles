using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Goggles.Desktop.ViewModels;
using Goggles.Desktop.Views;

namespace Goggles.Desktop;

/// <summary>
/// Given a page view model, returns its view.
/// </summary>
/// <remarks>
/// An explicit map rather than Avalonia's template, which derives the view's type name by string
/// replacement and creates it through reflection — that needs <c>RequiresUnreferencedCode</c> and
/// turns a renamed view into a runtime "Not Found" instead of a compile error.
/// </remarks>
public sealed class ViewLocator : IDataTemplate
{
    public Control? Build(object? param) => param switch
    {
        null => null,
        LoginViewModel => new LoginView(),
        LibraryViewModel => new LibraryView(),
        GameDetailViewModel => new GameDetailView(),
        _ => new TextBlock { Text = $"No view for {param.GetType().Name}" },
    };

    public bool Match(object? data) => data is ViewModelBase;
}
