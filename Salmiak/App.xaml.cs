using System.Windows;

namespace Salmiak;

public partial class App : System.Windows.Application
{
    static App()
    {
        System.AppContext.SetSwitch("Switch.System.Windows.Input.Stylus.DisableStylusAndTouchSupport", true);
    }
}
