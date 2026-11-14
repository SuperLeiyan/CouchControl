namespace CouchControl;

/// <summary>
/// Opens and closes the keyboard: CouchControl's own on-screen keyboard by default, or
/// Windows' touch keyboard when <see cref="AppConfig.UseWindowsTouchKeyboard"/> is set.
/// Windows' keyboard can't be typed on with a hidden Xbox controller: while it's open,
/// Windows cuts off that controller's input for everyone.
/// </summary>
internal static class KeyboardHost
{
    private static SynchronizationContext? _ui;
    private static Func<bool> _useBuiltIn = () => true;
    private static OnScreenKeyboardForm? _form;

    public static readonly OnScreenKeyboardModel Model = new();

    /// <summary>True while CouchControl's own keyboard is showing (safe from any thread).</summary>
    public static bool BuiltInOpen => Model.IsOpen;

    /// <summary>Call once on the UI thread.</summary>
    public static void Initialize(SynchronizationContext ui, Func<bool> useBuiltIn)
    {
        _ui = ui;
        _useBuiltIn = useBuiltIn;
        Model.CloseRequested += Close;
        Model.MoveRequested += () => Post(f => f.PlaceOnScreen());
    }

    /// <summary>Toggles the keyboard (any thread).</summary>
    public static void Toggle()
    {
        if (!_useBuiltIn())
        {
            if (Model.IsOpen) Close();
            TouchKeyboard.Toggle();
            return;
        }
        if (Model.IsOpen) Close();
        else Open();
    }

    public static void Open() => Post(f =>
    {
        if (Model.IsOpen) return;
        Model.OnOpened();
        f.PlaceOnScreen();
        f.Show();
        Model.IsOpen = true;
        AppLogger.Log("On-screen keyboard shown.");
    });

    public static void Close() => Post(f =>
    {
        if (!Model.IsOpen) return;
        Model.IsOpen = false;
        f.Hide();
        AppLogger.Log("On-screen keyboard hidden.");
    });

    private static void Post(Action<OnScreenKeyboardForm> action)
    {
        var ui = _ui;
        if (ui == null) return;
        ui.Post(_ =>
        {
            try
            {
                _form ??= new OnScreenKeyboardForm(Model);
                action(_form);
            }
            catch (Exception ex)
            {
                AppLogger.Log($"On-screen keyboard error: {ex.Message}");
            }
        }, null);
    }
}
