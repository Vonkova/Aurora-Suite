using System.IO;
using System.Windows;

namespace AuroraSuite
{
    public partial class App : System.Windows.Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // A silent crash during startup is much harder to diagnose than a visible
            // error - this is the difference between "the exe just doesn't open, no
            // error, no dialog" and an actual message telling you what broke.
            DispatcherUnhandledException += (_, args) =>
            {
                MessageBox.Show(
                    $"Unhandled error:\n\n{args.Exception}",
                    "AuroraSuite - Unhandled Exception", MessageBoxButton.OK, MessageBoxImage.Error);
                args.Handled = true;
            };

            // Create the default Image_Assets and Output folders next to the exe on
            // first launch, so they're already there to drop images into rather than
            // requiring the user to make them by hand (or hitting a "source folder
            // doesn't exist" error on the very first Convert click). Harmless no-op if
            // they already exist, or if the user later points the Image Assets tab at
            // a different folder entirely via Browse.
            try
            {
                Directory.CreateDirectory(Settings.DefaultImageAssetsSourcePath);
                Directory.CreateDirectory(Settings.DefaultImageAssetsOutputPath);
            }
            catch
            {
                // Non-fatal: worst case the user just has to create/browse to a
                // folder manually, same as before this existed.
            }
        }
    }
}
