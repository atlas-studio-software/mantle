using Mantle.App.Surfaces.Bar.Tasks;

namespace Mantle.App;

// STAND-IN for one App.xaml.cs line (native-r3-core-4 owns App.xaml.cs this round; requested in the
// native-bar-tasks report). At merge the coordinator deletes this file and adds, in HostStart after
// `_platform` is made:   _barTasks?.AttachOnHost(_platform, loop);
// (or the bar's surface context hands the platform over once the bar is a descriptor surface).
// Until then the bar calls this from its UI thread after OnLaunched has returned, so StartHost has
// already posted HostStart and the job below runs after it on the host thread.
public partial class MantleApp
{
    internal static void AttachBarTasks(BarTasksHost tasks)
    {
        if (Current is not MantleApp app || app._hostLoop is not { } loop) {
            Host.Log.Line("bar tasks: Mantle's host thread is not running, so the strip shows no apps");
            return;
        }
        loop.Post(() => {
            if (app._platform != null) tasks.AttachOnHost(app._platform, loop);
            else Host.Log.Line("bar tasks: the platform was not made (see the line above), so the strip shows no apps");
        });
    }
}
