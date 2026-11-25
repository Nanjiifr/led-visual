using LedVisual.App.Views;

namespace LedVisual.App;

internal static class Program
{
    private const string SingleInstanceMutexName = @"Local\LedVisual_SingleInstance_Mutex";

    [STAThread]
    private static void Main()
    {
        using var singleInstanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out var createdNew);
        if (!createdNew)
        {
            return;
        }

        ApplicationConfiguration.Initialize();

        using var context = new TrayApplicationContext();
        Application.Run(context);
    }
}
