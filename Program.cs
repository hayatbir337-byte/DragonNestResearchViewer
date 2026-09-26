using System.Text;

namespace DragonNestResearchViewer;

internal static class Program
{
    [STAThread]
    static void Main()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        ApplicationConfiguration.Initialize();

        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => ShowFatal(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex) ShowFatal(ex);
        };

        try
        {
            Application.Run(new ViewerForm());
        }
        catch (Exception ex)
        {
            ShowFatal(ex);
        }
    }

    static void ShowFatal(Exception ex)
    {
        try
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DragonNestResearchViewer");
            Directory.CreateDirectory(dir);
            string log = Path.Combine(dir, "startup-error.txt");
            File.WriteAllText(log, ex.ToString());

            MessageBox.Show(
                "Dragon Nest Research Viewer başlatılırken hata oluştu.\n\n" +
                ex.Message + "\n\nHata kaydı:\n" + log,
                "Dragon Nest Research Viewer V1",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        catch
        {
            MessageBox.Show(ex.ToString(), "Dragon Nest Research Viewer V1");
        }
    }
}
