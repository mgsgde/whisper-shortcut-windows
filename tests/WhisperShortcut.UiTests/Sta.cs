namespace WhisperShortcut.UiTests;

internal static class Sta
{
    /// <summary>WinForms needs an STA thread with a message loop context; xunit runs tests on MTA thread-pool threads.</summary>
    public static void Run(Action body)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
            try { body(); }
            catch (Exception e) { error = e; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(60))) throw new TimeoutException("UI test hung (window frozen?)");
        if (error != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
    }
}
