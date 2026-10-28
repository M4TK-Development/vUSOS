using ObjCRuntime;
using UIKit;

namespace Vusos.App;

public class Program
{
    // To jest punkt wejścia aplikacji.
    static void Main(string[] args)
    {
        UIApplication.Main(args, null, typeof(AppDelegate));
    }
}
